using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // A rope (or hose) from one end to the other through any number of points in between, on anything: moving
    // bodies, the world, things being carried. It runs freely over the points in between, as over 1:1 pulleys,
    // so it has one tension all along and only its total length is held: the sum of its runs can't exceed it.
    // It only pulls, and pulled harder than its breaking strength it snaps. Slack, each run hangs in a curve (a
    // shallow catenary: a run of length L across a gap d sags by about sqrt(3 d (L - d) / 8) in the middle), the
    // slack shared out between the runs by their lengths.
    //
    // Each point is on the thing it was tied to (the collider's own object, in its own space), and goes where that
    // thing goes: picked up, put down, locked into a load bed or fitted, it is still on it. What it pulls on is
    // whatever body that thing is part of at the moment.
    //
    // A rope stretches like a spring, as a real one does: its stiffness is its breaking strength over the stretch
    // it breaks at (the kit's 'stretch'), over its length, with damping near critical for what it pulls. Each
    // physics step that spring is worked as one constraint on the rope's length (a soft constraint, solved
    // implicitly so it is stable at any stiffness): the bodies at its points get the impulse it pulls with (through
    // their mass and turning inertia, at the points). Two heavy vehicles moving a little against each other on
    // their suspension pull on a hose between them only as hard as that small stretch does. A point on something with no body of its own, or a
    // kinematic one, is moved by something else: it pulls the rest of the rope along and nothing pulls it back.
    // A thing in the player's hands is held by the player: the rope pulls it no harder than a hand can hold
    // (HandForce), and a held thing can't be taken further than the rope reaches (it is held back at the end
    // of it, after the game places it each frame).
    internal sealed class Rope : MonoBehaviour
    {
        public static readonly List<Rope> All = new List<Rope>();

        private const int SegmentsPerRun = 12;
        private const float Damping = 1f;        // the rope's damping ratio, for the mass it pulls
        private const float HandForce = 400f;    // N: the most a held thing pulls (or is pulled) with
        private const float HeldSlack = 0.03f;   // m: a held thing is held back only past this much stretch
        private const float PulledFree = 0.3f;   // m: held back further than this from the hand, it is pulled out of it

        // a point the rope is tied to or runs over, on a thing, in that thing's own space
        public sealed class Point
        {
            public Transform T;
            public Vector3 Local;
            public Vector3 World => T.TransformPoint(Local);
            public Vector3 Velocity;      // of a point nothing pulls back (from its frames)
            public Vector3 Last;
            public bool Seen;
            // a grabbed thing's: where the hand puts it this frame, before it is held back to the rope's reach. The
            // rope works from there (that is how far and how fast the hand pulls); where it is held is only shown.
            public Vector3 Wanted;
            public int WantedFrame = -10;
            public Vector3 Pulled => WantedFrame >= Time.frameCount - 1 ? Wanted : World;
        }

        private readonly List<Point> _points = new List<Point>();
        private float _length, _tension;
        private RopeKind _kind;
        private LineRenderer _line;

        public RopeKind Kind => _kind;
        public IList<Point> Points => _points;
        public float Tension => _tension;

        public float Length
        {
            get { return _length; }
            set { _length = Mathf.Max(0.1f, value); }
        }

        public Vector3 EndA => _points.Count > 0 && _points[0].T != null ? _points[0].World : Vector3.zero;
        public Vector3 EndB => _points.Count > 0 && _points[_points.Count - 1].T != null ? _points[_points.Count - 1].World : Vector3.zero;

        // a rope through points (the thing each is on, and where, in the world), as long as its runs plus slack
        public static Rope Tie(RopeKind kind, IList<KeyValuePair<Transform, Vector3>> at, float slack)
        {
            var go = new GameObject(kind.IsHose ? "Hose" : "Rope");
            var rope = go.AddComponent<Rope>();
            rope._kind = kind;
            float length = 0f;
            for (int i = 0; i < at.Count; i++)
            {
                rope._points.Add(new Point { T = at[i].Key, Local = at[i].Key.InverseTransformPoint(at[i].Value) });
                if (i > 0) length += Vector3.Distance(at[i - 1].Value, at[i].Value);
            }
            rope._length = Mathf.Max(0.1f, length + slack);
            rope._line = go.AddComponent<LineRenderer>();
            rope._line.useWorldSpace = true;
            rope._line.numCapVertices = 2;
            rope._line.sharedMaterial = kind.Material();
            rope._line.textureMode = LineTextureMode.Tile;
            rope._line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (kind.IsHose) Hose.Fit(rope, kind);
            All.Add(rope);
            Plugin.Verbose(kind.Name + " tied, " + rope._length.ToString("0.00") + " m, stiffness " + (rope.Stiffness / 1000f).ToString("0.0") + " kN/m, breaks at "
                + kind.Strength.ToString("0.##") + " kN: " + string.Join(" - ", rope._points.ConvertAll(rope.Describe).ToArray()));
            return rope;
        }

        // N/m: breaking strength over the stretch at break, along the whole length
        private float Stiffness => _kind.Strength * 1000f / Mathf.Max(1e-4f, _kind.Elongation * _length);

        // a point, for the log: what it is on and what it pulls on
        private string Describe(Point p)
        {
            if (p.T == null) return "(gone)";
            var rb = Body(p.T);
            string on = rb != null ? rb.name + " " + rb.mass.ToString("0.#") + " kg" : Held(p.T, out _) ? "held" : "fixed";
            return p.T.name + " [" + on + "]";
        }

        // the bodies the points pull on, to log when they change (picked up, put down, locked, fitted)
        private string _bodies;

        private void LogBodies()
        {
            if (!Plugin.VerboseLogEntry.Value) return;
            string now = string.Join(" - ", _points.ConvertAll(Describe).ToArray());
            if (now == _bodies) return;
            if (_bodies != null) Plugin.Verbose(_kind.Name + " now on: " + now);
            _bodies = now;
        }

        private static GameObject Global(string name) => Hands.Global(name);

        private static bool Held(Transform t, out bool grabbed)
        {
            grabbed = Hands.Grabbed(t);
            return Hands.Held(t);
        }

        // the body a point pulls on: the moving body its thing is part of; none for the world, a kinematic body
        // or a held thing
        private static Rigidbody Body(Transform t)
        {
            if (t == null || Held(t, out _)) return null;
            var rb = t.GetComponentInParent<Rigidbody>();
            return rb != null && !rb.isKinematic ? rb : null;
        }

        // cut by the player
        public void CutBy(string why)
        {
            Plugin.Verbose(_kind.Name + " " + why);
            Cut();
        }

        public void Cut()
        {
            All.Remove(this);
            Destroy(gameObject);
        }

        private bool Broken()
        {
            foreach (var p in _points) if (p.T == null) return true;
            return false;
        }

        // per body: the rope's pull direction summed over its points (linear, and turning about its centre)
        private readonly Dictionary<Rigidbody, Vector3> _lin = new Dictionary<Rigidbody, Vector3>();
        private readonly Dictionary<Rigidbody, Vector3> _ang = new Dictionary<Rigidbody, Vector3>();
        private Vector3[] _world = new Vector3[0], _grad = new Vector3[0];

        private void FixedUpdate()
        {
            if (_points.Count < 2 || Broken()) return;
            float dt = Time.fixedDeltaTime;
            int n = _points.Count;
            if (_world.Length != n) { _world = new Vector3[n]; _grad = new Vector3[n]; }
            float total = 0f;
            for (int i = 0; i < n; i++) _world[i] = _points[i].Pulled;
            for (int i = 0; i < n; i++) _grad[i] = Vector3.zero;
            for (int i = 0; i + 1 < n; i++)
            {
                Vector3 run = _world[i + 1] - _world[i];
                float d = run.magnitude;
                total += d;
                if (d < 1e-4f) continue;
                Vector3 u = run / d;          // from i towards i + 1
                _grad[i] -= u;                // moving i away from i + 1 lengthens the rope
                _grad[i + 1] += u;
            }
            float stretch = total - _length;
            if (stretch <= 0f) { _tension = Mathf.Lerp(_tension, 0f, 0.5f); return; }   // slack: no pull

            // how fast the rope is lengthening, and how hard it is to stop (its bodies' mass at the points)
            _lin.Clear(); _ang.Clear();
            float rate = 0f;
            bool held = false;
            for (int i = 0; i < n; i++)
            {
                var p = _points[i];
                var rb = Body(p.T);
                if (rb == null)
                {
                    held |= Held(p.T, out _);
                    rate += Vector3.Dot(_grad[i], p.Velocity);
                    continue;
                }
                rate += Vector3.Dot(_grad[i], rb.GetPointVelocity(_world[i]));
                _lin.TryGetValue(rb, out var l);
                _ang.TryGetValue(rb, out var a);
                _lin[rb] = l + _grad[i];
                _ang[rb] = a + Vector3.Cross(_world[i] - rb.worldCenterOfMass, _grad[i]);
            }
            float k = 0f;
            foreach (var kv in _lin)
            {
                var rb = kv.Key;
                Vector3 g = kv.Value, ga = _ang[rb];
                k += g.sqrMagnitude / rb.mass + Vector3.Dot(ga, InverseInertia(rb, ga));
            }
            if (k <= 0f) { _tension = 0f; return; }

            // the spring's impulse this step (a soft constraint: spring and damper for the mass it pulls, implicit)
            float stiffness = Stiffness;
            float damper = 2f * Damping * Mathf.Sqrt(stiffness / k);
            float soft = damper + dt * stiffness;
            float gamma = 1f / (dt * soft), beta = dt * stiffness / soft;
            float impulse = (rate + beta * stretch / dt) / (k + gamma);
            if (impulse <= 0f) { _tension = Mathf.Lerp(_tension, 0f, 0.5f); return; }
            if (held) impulse = Mathf.Min(impulse, HandForce * dt);
            // snapping: on the pull, held over a few steps (not a single step's spike); it never pulls harder than
            // it breaks at
            _tension = Mathf.Lerp(_tension, impulse / dt, 0.35f);
            if (_tension > _kind.Strength * 1000f)
            {
                Plugin.Verbose(_kind.Name + " snapped at " + (_tension / 1000f).ToString("0.00") + " kN: stretched " + (stretch * 1000f).ToString("0") + " mm over "
                    + _length.ToString("0.00") + " m, lengthening at " + rate.ToString("0.00") + " m/s; " + string.Join(" - ", _points.ConvertAll(Describe).ToArray()));
                Cut();
                return;
            }
            impulse = Mathf.Min(impulse, _kind.Strength * 1000f * dt);
            foreach (var kv in _lin)
            {
                var rb = kv.Key;
                rb.velocity -= impulse * kv.Value / rb.mass;
                rb.angularVelocity -= InverseInertia(rb, impulse * _ang[rb]);
                rb.WakeUp();
            }
        }

        // a body's inverse inertia (world space) applied to v
        private static Vector3 InverseInertia(Rigidbody rb, Vector3 v)
        {
            Quaternion q = rb.rotation * rb.inertiaTensorRotation;
            Vector3 local = Quaternion.Inverse(q) * v, inertia = rb.inertiaTensor;
            local.x = inertia.x > 0f ? local.x / inertia.x : 0f;
            local.y = inertia.y > 0f ? local.y / inertia.y : 0f;
            local.z = inertia.z > 0f ? local.z / inertia.z : 0f;
            return q * local;
        }

        // Without holding by force (Hands.HoldByForce off): a grabbed thing (the game places it in the hand every
        // frame, after which this runs): where the hand put it
        // is kept (Point.Wanted: the rope pulls the rest from there, as hard as a hand can), and the thing is held
        // back to the end of the rope, along the rope, as far as the rest hasn't followed. Held back too far (the
        // rope pulls harder than a hand holds), it is pulled out of the hand.
        public static void HoldBack(GameObject item)
        {
            // held by force (HandHold), a grabbed thing is a body like any other: the rope pulls on it physically
            if (item == null || All.Count == 0 || HandHold.On) return;
            foreach (var rope in All)
            {
                if (rope == null || rope.Broken()) continue;
                foreach (var p in rope._points)
                    if (p.T.IsChildOf(item.transform)) { p.Wanted = p.World; p.WantedFrame = Time.frameCount; }
            }
            foreach (var rope in All)
            {
                if (rope == null || rope._points.Count < 2 || rope.Broken()) continue;
                for (int i = 0; i < rope._points.Count; i++)
                {
                    var p = rope._points[i];
                    if (!p.T.IsChildOf(item.transform)) continue;
                    float total = 0f;
                    for (int j = 0; j + 1 < rope._points.Count; j++) total += Vector3.Distance(rope._points[j].World, rope._points[j + 1].World);
                    float over = total - rope._length - HeldSlack;
                    if (over <= 0f) continue;
                    // the way that point lengthens the rope, and back along it by the stretch
                    Vector3 here = p.World, g = Vector3.zero;
                    if (i > 0) g += (here - rope._points[i - 1].World).normalized;
                    if (i + 1 < rope._points.Count) g += (here - rope._points[i + 1].World).normalized;
                    if (g.sqrMagnitude < 1e-6f) continue;
                    Vector3 back = g * (over / g.sqrMagnitude);
                    item.transform.position -= back;
                    // more than a hand can hold: pulled out of it (the grab's own drop)
                    if (back.magnitude > PulledFree) { LetGo(item); return; }
                }
            }
        }

        private static void LetGo(GameObject item) => Hands.LetGo(item, "a rope pulls harder than a hand holds");

        private void LateUpdate()
        {
            // a point's thing gone (destroyed, unloaded): the rope goes with it
            if (_points.Count < 2 || Broken())
            {
                Plugin.Verbose(_kind.Name + " gone: " + (_points.Count < 2 ? "fewer than two points" : "point " + _points.FindIndex(x => x.T == null) + "'s thing was destroyed or unloaded"));
                Cut();
                return;
            }
            LogBodies();

            // how the points nothing pulls back are moving (a carried thing, a vehicle part with no body)
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            foreach (var p in _points)
            {
                Vector3 w = p.Pulled;
                p.Velocity = p.Seen ? (w - p.Last) / dt : Vector3.zero;
                p.Last = w;
                p.Seen = true;
            }
            Draw();
        }

        private void Draw()
        {
            int n = _points.Count;
            float total = 0f;
            for (int i = 0; i + 1 < n; i++) total += Vector3.Distance(_points[i].World, _points[i + 1].World);
            float slack = Mathf.Max(0f, _length - total);
            _line.positionCount = SegmentsPerRun * (n - 1) + 1;
            _line.startWidth = _line.endWidth = _kind.Thickness;
            int k = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                Vector3 pa = _points[i].World, pb = _points[i + 1].World;
                float d = Vector3.Distance(pa, pb);
                float share = total > 1e-4f ? slack * d / total : slack / (n - 1);
                float sag = Mathf.Min(Mathf.Sqrt(3f * d * share / 8f), (d + share) * 0.5f);
                for (int s = (i == 0 ? 0 : 1); s <= SegmentsPerRun; s++)
                {
                    float t = (float)s / SegmentsPerRun;
                    _line.SetPosition(k++, Vector3.Lerp(pa, pb, t) + Vector3.down * (sag * 4f * t * (1f - t)));
                }
            }
        }

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
    }

    // The grab places the grabbed thing in the hand each frame (its 'ItemInHand' state: SetPosition in late update);
    // straight after, a roped one is held back to the rope's reach.
    [HarmonyPatch(typeof(SetPosition), "DoSetPosition")]
    internal static class HoldBackGrabbed
    {
        private static void Postfix(SetPosition __instance)
        {
            if (Rope.All.Count == 0 || __instance.Fsm == null || __instance.Fsm.Name != "GrabItem") return;
            try { Rope.HoldBack(__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject)); }
            catch (System.Exception e) { Plugin.Verbose("rope hold back: " + e.Message); }
        }
    }
}
