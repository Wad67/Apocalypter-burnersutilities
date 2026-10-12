using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // A rope tied between two points on anything: two moving bodies, a body and the world, or two fixed
    // things (then it only hangs there). A rope only pulls: it keeps the two ends from getting further apart
    // than its length and lets them come together freely, swing and turn. Pulled harder than its breaking
    // strength it snaps. Drawn as a line that hangs in a curve when slack (a shallow catenary: a rope of
    // length L across a gap d sags by about sqrt(3 d (L - d) / 8) in the middle).
    internal sealed class Rope : MonoBehaviour
    {
        public static readonly List<Rope> All = new List<Rope>();

        private const int Segments = 24;

        // each end: the thing it is tied to and the point on it, in that thing's own space
        private Transform _a, _b;
        private Vector3 _localA, _localB;
        private float _length;
        private RopeKind _kind;
        private ConfigurableJoint _joint;
        private LineRenderer _line;

        public float Length
        {
            get { return _length; }
            set
            {
                _length = Mathf.Max(0.1f, value);
                if (_joint != null) _joint.linearLimit = new SoftJointLimit { limit = _length };
            }
        }

        public Vector3 EndA { get { return _a != null ? _a.TransformPoint(_localA) : Vector3.zero; } }
        public Vector3 EndB { get { return _b != null ? _b.TransformPoint(_localB) : Vector3.zero; } }

        public static Rope Tie(RopeKind kind, Collider ca, Vector3 pa, Collider cb, Vector3 pb, float slack)
        {
            var go = new GameObject("Rope");
            var rope = go.AddComponent<Rope>();
            rope._kind = kind;
            // tied to the moving body a collider is part of, else to the collider itself
            rope._a = ca.attachedRigidbody != null ? ca.attachedRigidbody.transform : ca.transform;
            rope._b = cb.attachedRigidbody != null ? cb.attachedRigidbody.transform : cb.transform;
            rope._localA = rope._a.InverseTransformPoint(pa);
            rope._localB = rope._b.InverseTransformPoint(pb);
            rope._length = Mathf.Max(0.1f, Vector3.Distance(pa, pb) + slack);
            rope._line = go.AddComponent<LineRenderer>();
            rope._line.useWorldSpace = true;
            rope._line.positionCount = Segments + 1;
            rope._line.numCapVertices = 2;
            rope._line.sharedMaterial = kind.Material();
            rope._line.textureMode = LineTextureMode.Tile;
            rope._line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            rope.Bind(Body(rope._a), Body(rope._b));
            All.Add(rope);
            Plugin.Verbose(kind.Name + " tied: " + rope._a.name + " - " + rope._b.name + ", " + rope._length.ToString("0.00") + " m"
                + (rope._joint != null ? "" : " (both ends fixed)"));
            return rope;
        }

        // The body each end is part of right now: the Rigidbody it or a parent has, none for the world or for a
        // thing that has no body of its own at the moment (an item in the player's hands has its Rigidbody taken
        // away; one the player holds is never tied to the player's own body).
        private static Rigidbody Body(Transform t)
        {
            var rb = t != null ? t.GetComponentInParent<Rigidbody>() : null;
            if (rb == null) return null;
            var player = FsmVariables.GlobalVariables.FindFsmGameObject("Player");
            if (player != null && player.Value != null && rb.transform.IsChildOf(player.Value.transform)) return null;
            return rb;
        }

        // The joint goes on an end that can move and holds it to the other end: the other body, or, when the
        // other end has no body, the point where that end is, followed every physics step (fixed for the world,
        // moving for a thing being carried, which drags the roped thing along).
        private Rigidbody _bodyA, _bodyB;
        private bool _ownerIsA;

        private void Bind(Rigidbody ra, Rigidbody rb)
        {
            _bodyA = ra; _bodyB = rb;
            if (ra == rb) return;                    // both ends on one body (or neither has one): nothing to hold
            _ownerIsA = ra != null && (rb == null || !ra.isKinematic || rb.isKinematic);
            var body = _ownerIsA ? ra : rb;
            var connected = _ownerIsA ? rb : ra;
            Vector3 ownerPoint = _ownerIsA ? EndA : EndB, otherPoint = _ownerIsA ? EndB : EndA;
            _joint = body.gameObject.AddComponent<ConfigurableJoint>();
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = body.transform.InverseTransformPoint(ownerPoint);
            _joint.connectedBody = connected;
            _joint.connectedAnchor = connected != null ? connected.transform.InverseTransformPoint(otherPoint) : otherPoint;
            _joint.xMotion = _joint.yMotion = _joint.zMotion = ConfigurableJointMotion.Limited;
            _joint.angularXMotion = _joint.angularYMotion = _joint.angularZMotion = ConfigurableJointMotion.Free;
            _joint.linearLimit = new SoftJointLimit { limit = _length };
            _joint.enableCollision = true;
            _joint.enablePreprocessing = false;
            _joint.breakForce = _kind.Strength * 1000f;
            _joint.breakTorque = float.PositiveInfinity;
            _bound = true;
        }

        // its joint has to go now (the body it is on is about to lose its Rigidbody, or an end changed body); it
        // ties on again to whatever the ends are part of
        public void Unbind()
        {
            if (_joint != null) DestroyImmediate(_joint);
            _joint = null;
            _bound = false;   // let go on purpose, not snapped
        }

        public void Cut()
        {
            Unbind();
            All.Remove(this);
            Destroy(gameObject);
        }

        private void FixedUpdate()
        {
            if (_a == null || _b == null) return;
            // the joint gone by itself: pulled past its strength
            if (_bound && _joint == null)
            {
                Plugin.Verbose(_kind.Name + " snapped: " + _a.name + " - " + _b.name);
                Cut();
                return;
            }
            // an end picked up, put down, fitted or locked down: tie on again to what the ends are now
            Rigidbody ra = Body(_a), rb = Body(_b);
            if (_joint != null && (ra != _bodyA || rb != _bodyB)) Unbind();
            if (_joint == null && (ra != _bodyA || rb != _bodyB || !_bound)) Bind(ra, rb);
            // an end with no body is a point the other end is held to, wherever it is now
            if (_joint != null && _joint.connectedBody == null) _joint.connectedAnchor = _ownerIsA ? EndB : EndA;
        }

        private void LateUpdate()
        {
            // an end gone (destroyed, unloaded): the rope goes with it
            if (_a == null || _b == null) { Cut(); return; }

            Vector3 pa = EndA, pb = EndB;
            float d = Vector3.Distance(pa, pb);
            float sag = d < _length ? Mathf.Sqrt(3f * d * (_length - d) / 8f) : 0f;
            sag = Mathf.Min(sag, _length * 0.5f);
            float width = _kind.Thickness;
            _line.startWidth = _line.endWidth = width;
            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                _line.SetPosition(i, Vector3.Lerp(pa, pb, t) + Vector3.down * (sag * 4f * t * (1f - t)));
            }
        }

        private bool _bound;

        private void OnDestroy() { All.Remove(this); }

        // the rope nearest a ray, within `within` of it and `reach` along it
        public static Rope Aimed(Ray ray, float reach, float within)
        {
            Rope best = null;
            float bestD = within;
            foreach (var r in All)
            {
                if (r == null || r._line == null) continue;
                for (int i = 0; i < r._line.positionCount - 1; i++)
                {
                    float d = RaySegment(ray, reach, r._line.GetPosition(i), r._line.GetPosition(i + 1));
                    if (d < bestD) { bestD = d; best = r; }
                }
            }
            return best;
        }

        private static float RaySegment(Ray ray, float reach, Vector3 p, Vector3 q)
        {
            // closest points between the ray (0..reach) and the segment, by sampling the segment
            float best = float.MaxValue;
            for (int k = 0; k <= 4; k++)
            {
                var x = Vector3.Lerp(p, q, k / 4f);
                float along = Vector3.Dot(x - ray.origin, ray.direction);
                if (along < 0f || along > reach) continue;
                best = Mathf.Min(best, Vector3.Distance(x, ray.origin + ray.direction * along));
            }
            return best;
        }

        // ropes tied to a thing that is about to lose its Rigidbody (fitted as a part, locked into a load bed)
        public static void Release(GameObject go)
        {
            if (go == null) return;
            foreach (var r in All.ToArray())
                if (r != null && r._joint != null && r._joint.gameObject == go) r.Unbind();
        }
    }

    // The game takes an item's Rigidbody away when it is fitted or locked down (a DestroyComponent action),
    // which Unity refuses while a rope's joint depends on it. The rope lets go first and ties on again to what
    // the item became part of.
    [HarmonyPatch(typeof(DestroyComponent), "OnEnter")]
    internal static class ReleaseBeforeRigidbodyGoes
    {
        private static void Prefix(DestroyComponent __instance)
        {
            try
            {
                if (Rope.All.Count == 0 || __instance.component == null) return;
                var name = __instance.component.Value;
                if (string.IsNullOrEmpty(name) || !name.EndsWith("Rigidbody")) return;
                Rope.Release(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject));
            }
            catch (System.Exception e) { Plugin.Verbose("rope release: " + e.Message); }
        }
    }
}
