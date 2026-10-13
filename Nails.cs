using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // Nails. A nail goes in along the aim: through the first thing it hits and on into whatever is behind it, as
    // far as its length reaches (it has to bite at least MinBite into it). Whatever is behind without a body of its
    // own (a wall, the ground) holds it as the world does. A nail that doesn't come out the far side, or meets
    // nothing behind, is only stuck in the first thing.
    //
    // What nails hold, between each two bodies (or a body and the world):
    //   one nail, or nails all on one line: a pivot, turning freely about the nail
    //   two or more otherwise: fixed together, as welded or bolted
    // A joint between the two bodies at the nails, which tears out (every nail in it) when pulled harder than the
    // nails hold: each takes its shear strength, and twisting it is resisted by the nails' bending (a single nail)
    // or by their pull across their spread (several).
    //
    // Each nail stays in the two things it went into and follows what they become: picked up, put down, locked
    // into a load bed or fitted, the joint is made again between whatever bodies they are part of then. Two
    // things on one body need no joint. A nailed thing can't be carried off if what it is nailed to is fixed or
    // heavier than a hand lifts: the grab lets go of it.
    internal sealed class Nail
    {
        public Transform A, B;           // what it went through, and into (null: nothing behind)
        public Vector3 HeadA, PivotA;    // in A's space: the head, and where it crosses into B
        public Vector3 PivotB;           // in B's space
        public Vector3 DirA;             // in A's space: the way it points
        public float Shear;              // N
        public GameObject Shown;

        public Vector3 Head => A.TransformPoint(HeadA);
        public Vector3 Pivot => A.TransformPoint(PivotA);
        public Vector3 Dir => A.TransformDirection(DirA).normalized;
        public bool Gone => A == null || Shown == null || (B == null && HasB);
        public bool HasB;                // it went into something behind (gone if that thing is)
    }

    internal sealed class NailBoard : MonoBehaviour
    {
        public static readonly List<Nail> All = new List<Nail>();
        private const float HandLift = 400f / 9.81f;   // kg a hand lifts
        private const float BendLever = 0.02f;         // m: a single nail's resistance to being bent over

        // a joint between two bodies, for the nails between them
        private sealed class Fastening
        {
            public Rigidbody On, To;
            public ConfigurableJoint Joint;
            public string Shape;           // what it was made for (nails, how), to see when to make it again
            public List<Nail> Nails = new List<Nail>();
        }

        private readonly Dictionary<string, Fastening> _fastenings = new Dictionary<string, Fastening>();

        // the body a thing is part of; null for the world
        private static Rigidbody Body(Transform t) => t != null ? t.GetComponentInParent<Rigidbody>() : null;

        private static string Key(Rigidbody a, Rigidbody b)
        {
            int ia = a != null ? a.GetInstanceID() : 0, ib = b != null ? b.GetInstanceID() : 0;
            return ia < ib ? ia + ":" + ib : ib + ":" + ia;
        }

        private void FixedUpdate()
        {
            All.RemoveAll(n => n.Gone);
            // the nails between each two bodies now
            var now = new Dictionary<string, Fastening>();
            foreach (var n in All.ToList())
            {
                if (n.B == null) continue;
                if (!Carried(n)) continue;
                Rigidbody a = Body(n.A), b = Body(n.B);
                if (a == b) continue;
                string key = Key(a, b);
                if (!now.TryGetValue(key, out var f)) now[key] = f = new Fastening { On = a, To = b };
                f.Nails.Add(n);
            }
            // gone, or torn out
            foreach (var kv in _fastenings.ToList())
            {
                var old = kv.Value;
                // the joint gone by itself (its reference still set, the object destroyed): pulled past its strength
                bool tornOut = !ReferenceEquals(old.Joint, null) && old.Joint == null;
                if (tornOut)
                {
                    Plugin.Verbose($"nails torn out: {old.Nails.Count} between {Name(old.On)} and {Name(old.To)}");
                    foreach (var n in old.Nails) Remove(n, false);
                    Sounds.Play("knife_hit", old.Nails.Count > 0 && old.Nails[0].A != null ? old.Nails[0].Pivot : Vector3.zero, 0.6f);
                    _fastenings.Remove(kv.Key);
                    now.Remove(kv.Key);
                    continue;
                }
                if (!now.ContainsKey(kv.Key)) { Unmake(old); _fastenings.Remove(kv.Key); }
            }
            foreach (var kv in now)
            {
                var f = kv.Value;
                f.Nails.RemoveAll(n => n.Gone);
                if (f.Nails.Count == 0) continue;
                string shape = Shape(f);
                if (_fastenings.TryGetValue(kv.Key, out var had) && had.Shape == shape && (had.Joint != null || shape == "none"))
                {
                    had.Nails = f.Nails;
                    continue;
                }
                if (had != null) Unmake(had);
                f.Shape = shape;
                Make(f);
                _fastenings[kv.Key] = f;
            }
        }

        private static string Name(Rigidbody rb) => rb != null ? rb.name : "the world";

        // what the joint has to be: which body it goes on, a pivot or fixed, and which nails
        private static string Shape(Fastening f)
        {
            bool aMoves = f.On != null && !f.On.isKinematic, bMoves = f.To != null && !f.To.isKinematic;
            if (!aMoves && !bMoves) return "none";
            return (Pivot(f.Nails) ? "pivot" : "fixed") + ":" + string.Join(",", f.Nails.Select(n => n.GetHashCode().ToString()).ToArray());
        }

        // all on the nail's own line: a pivot about it
        private static bool Pivot(List<Nail> nails)
        {
            var first = nails[0];
            Vector3 p0 = first.Pivot, d0 = first.Dir;
            foreach (var n in nails)
            {
                if (Vector3.Angle(n.Dir, d0) > 5f && Vector3.Angle(n.Dir, -d0) > 5f) return false;
                Vector3 off = n.Pivot - p0;
                if ((off - Vector3.Dot(off, d0) * d0).magnitude > 0.005f) return false;
            }
            return true;
        }

        private static void Make(Fastening f)
        {
            if (f.Shape == "none") return;
            // on a body that moves, held to the other (or the world)
            bool onA = f.On != null && !f.On.isKinematic;
            var body = onA ? f.On : f.To;
            var other = onA ? f.To : f.On;
            // where the nails are on each side, each in its own thing (so a side moved off them is drawn back)
            Vector3 at = Vector3.zero, to = Vector3.zero;
            foreach (var n in f.Nails)
            {
                Vector3 pa = n.Pivot, pb = n.B.TransformPoint(n.PivotB);
                bool aIsBody = Body(n.A) == body;
                at += aIsBody ? pa : pb;
                to += aIsBody ? pb : pa;
            }
            at /= f.Nails.Count;
            to /= f.Nails.Count;
            var j = body.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.anchor = body.transform.InverseTransformPoint(at);
            j.connectedBody = other;
            j.connectedAnchor = other != null ? other.transform.InverseTransformPoint(to) : to;
            Vector3 axis = f.Nails[0].Dir;
            j.axis = body.transform.InverseTransformDirection(axis);
            j.secondaryAxis = body.transform.InverseTransformDirection(Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized);
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            bool pivot = f.Shape.StartsWith("pivot");
            j.angularXMotion = pivot ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Locked;
            j.enableCollision = false;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.01f;
            j.projectionAngle = 1f;
            float shear = f.Nails.Sum(n => n.Shear);
            float spread = 0f;
            foreach (var n in f.Nails) spread = Mathf.Max(spread, Vector3.Distance(n.Pivot, at));
            j.breakForce = shear;
            j.breakTorque = shear * (pivot ? BendLever : Mathf.Max(BendLever, spread));
            f.Joint = j;

            Plugin.Verbose($"nails: {Name(f.On)} to {Name(f.To)}, {f.Nails.Count} nail(s), {(pivot ? "a pivot" : "fixed")}; holds {shear / 1000f:0.0} kN, {j.breakTorque:0} N m");
        }

        private static void Unmake(Fastening f)
        {
            if (f.Joint != null) Object.Destroy(f.Joint);
            f.Joint = null;
            f.Shape = null;
        }

        // Without holding by force (Hands.HoldByForce off): a nail into a grabbed thing (the game places it in the
        // hand each frame, not physics). Nailed to something
        // fixed, or heavier than a hand lifts, it can't be carried off: the grab lets go of it. Nailed to something
        // light, it comes along hanging from the nails if it has a body of its own; without one (it was locked
        // into a load bed) there is nothing to hang it from, and pulling it away pulls the nail out.
        // False: this nail holds no joint now.
        private static bool Carried(Nail n)
        {
            if (HandHold.On) return true;   // held by force, a grabbed thing is a body like any other
            bool ga = Hands.Grabbed(n.A), gb = Hands.Grabbed(n.B);
            if (!ga && !gb) return true;
            if (ga && gb) return false;
            Transform held = ga ? n.A : n.B, other = ga ? n.B : n.A;
            var hand = Hands.Global("Hand");
            var own = Body(held);
            if (own != null && (hand == null || !own.transform.IsChildOf(hand.transform))) own = null;
            var rb = Body(other);
            if (rb == null || rb.isKinematic || rb.mass > HandLift)
            {
                Hands.LetGo(held.gameObject, "nailed to " + (rb == null ? "something fixed" : rb.name + ", " + rb.mass.ToString("0") + " kg"));
                return false;
            }
            if (own == null)
            {
                Plugin.Verbose("nail pulled out: " + held.name + " carried away from " + other.name);
                Remove(n, false);
                return false;
            }
            return true;
        }

        // the game takes a thing's Rigidbody away when it is locked into a load bed or fitted, which Unity refuses
        // while a joint is on it: the nails let go first, and join it again to what it became part of
        public static NailBoard Instance;
        private void Awake() { Instance = this; }

        public static void Release(GameObject go)
        {
            if (go == null || Instance == null) return;
            foreach (var f in Instance._fastenings.Values)
            {
                if (f.Joint == null || (f.Joint.gameObject != go && (f.Joint.connectedBody == null || f.Joint.connectedBody.gameObject != go))) continue;
                Object.DestroyImmediate(f.Joint);
                f.Joint = null;
                f.Shape = null;
            }
        }

        public static void Add(Nail n)
        {
            All.Add(n);
        }

        public static void Remove(Nail n, bool log = true)
        {
            All.Remove(n);
            if (n.Shown != null) Object.Destroy(n.Shown);
            n.Shown = null;
            if (log) Plugin.Verbose("nail pulled out of " + (n.A != null ? n.A.name : "?") + (n.B != null ? " and " + n.B.name : ""));
        }

        // the nail whose head the aim passes nearest, within `within`
        public static Nail Aimed(Ray ray, float reach, float within)
        {
            Nail best = null;
            float bestD = within;
            foreach (var n in All)
            {
                if (n.Gone) continue;
                Vector3 h = n.Head;
                float along = Vector3.Dot(h - ray.origin, ray.direction);
                if (along < 0f || along > reach) continue;
                float d = Vector3.Distance(h, ray.origin + ray.direction * along);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }

        // how two things are held now, for the gun's instructions
        public static string Describe(Nail n)
        {
            if (n.B == null) return "Stuck in " + n.A.name + " only: it came out into nothing.";
            Rigidbody a = Body(n.A), b = Body(n.B);
            if (a == b) return $"{n.A.name} to {n.B.name}: one body already, nothing to hold.";
            int count = All.Count(x => x.B != null && !x.Gone && Key(Body(x.A), Body(x.B)) == Key(a, b));
            var list = All.Where(x => x.B != null && !x.Gone && Key(Body(x.A), Body(x.B)) == Key(a, b)).ToList();
            return $"{n.A.name} to {n.B.name}: {count} nail(s), {(Pivot(list) ? "pivoting" : "fixed")}.";
        }
    }

    // The game takes a thing's Rigidbody away (a DestroyComponent action) when it is locked into a load bed or
    // fitted; Unity refuses while a joint is on it, so nail joints let go of it first.
    [HarmonyPatch(typeof(DestroyComponent), "OnEnter")]
    internal static class ReleaseNailsBeforeRigidbodyGoes
    {
        private static void Prefix(DestroyComponent __instance)
        {
            try
            {
                if (NailBoard.All.Count == 0 || __instance.component == null) return;
                var name = __instance.component.Value;
                if (string.IsNullOrEmpty(name) || !name.EndsWith("Rigidbody")) return;
                NailBoard.Release(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject));
            }
            catch (System.Exception e) { Plugin.Verbose("nail release: " + e.Message); }
        }
    }

    // the game's sounds, by name, found once
    internal static class Sounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();

        public static AudioClip Clip(string name)
        {
            if (!Clips.TryGetValue(name, out var clip))
            {
                clip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(x => x.name == name);
                if (clip == null) Plugin.Log.LogWarning("no sound '" + name + "'");
                Clips[name] = clip;
            }
            return clip;
        }

        public static void Play(string name, Vector3 at, float volume)
        {
            var clip = Clip(name);
            if (clip != null) AudioSource.PlayClipAtPoint(clip, at, volume);
        }
    }
}
