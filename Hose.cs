using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace BurnersUtilities
{
    // A hose: a rope from a kit with a bore (RopeKinds), that also carries liquid between its two ends (the points
    // it runs over in between only route it). Each end is in the liquid container it was tied to: the game's own
    // kind, a thing with a LiquidAmount FSM (Liquid, LiquidCapacity, in litres), which is what the game's cans pour
    // into, holding what it is tagged with (Gasoline, Diesel, MotorOil, Water), or, untagged (a dew collector's
    // water), what it pours into itself (its UseCan FSM's trigger tag). An end tied to the thing a container sits
    // in (a dew collector, around its water) is put on that container, at its top; with more than one, the one
    // nearest where it was tied. Two containers holding the same liquid, and the liquid
    // runs downhill through the hose: from the one whose surface is higher, out of the other end, until the source
    // runs dry, the other is full, or the surface has come down to the outlet (or up to it, filling a container
    // from below). The hose is taken as primed.
    //
    // The flow is the pipe flow through the hose for that head: the head goes into the speed of the liquid, the
    // loss at the inlet and the friction along the hose's length (laminar, or turbulent by Blasius, from the
    // liquid's viscosity). A surface is estimated from the container's shape: as far up its bounds as it is full.
    // While it flows, the game's own pouring sound for that liquid plays at the outlet. Viscosity and sound per
    // liquid are in the rope kit table ([liquid: <tag>]).
    [RequireComponent(typeof(Rope))]
    internal sealed class Hose : MonoBehaviour
    {
        private const float InletLoss = 0.5f;     // a sharp-edged inlet's loss, velocity heads
        private const float MinHead = 0.005f;     // below this the flow has stopped, m
        private const float Hearing = 20f;        // the pouring sound fades out over this distance, m
        private const float FullFlow = 1f;        // L/s at full volume: the game's cans pour at 1 L/s with that sound
        private const float Quiet = 0.002f;       // L/s: below this it is silent
        private const float Smoothing = 0.5f;     // s: what it carries is averaged over about this long

        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();

        // one end's container
        private sealed class Container
        {
            public GameObject Go;
            public FsmFloat Amount, Capacity;
            public string Tag;
            public Collider[] Shape;

            public bool Valid => Go != null && Amount != null && Capacity != null;
            public float Fill => Capacity.Value > 0f ? Mathf.Clamp01(Amount.Value / Capacity.Value) : 0f;

            // the liquid's surface: as far up the container's bounds as it is full
            public float Surface
            {
                get
                {
                    bool any = false;
                    var b = new Bounds();
                    foreach (var c in Shape)
                    {
                        if (c == null || !c.enabled) continue;
                        if (!any) { b = c.bounds; any = true; }
                        else b.Encapsulate(c.bounds);
                    }
                    if (!any) return Go.transform.position.y;
                    return b.min.y + Fill * b.size.y;
                }
            }
        }

        private Rope _rope;
        private RopeKind _kind;
        private Container _a, _b;
        private AudioSource _audio;
        private float _flow;          // L/s the hose would carry, from a to b (negative: b to a)
        private float _moved;         // L/s it does carry (no more than the source has or the other takes), smoothed
        private string _logged;       // what it was last logged doing
        private float _logAt;

        public static void Fit(Rope rope, RopeKind kind)
        {
            var hose = rope.gameObject.AddComponent<Hose>();
            hose._rope = rope;
            hose._kind = kind;
            hose._a = In(rope.Points[0].T);
            hose._b = In(rope.Points[rope.Points.Count - 1].T);
            Plugin.Verbose($"{kind.Name}: {Describe(hose._a)} - {Describe(hose._b)}");
        }

        private static string Describe(Container c) => c == null ? "no container" : $"{c.Go.name} ({c.Tag}, {c.Amount.Value:0.0} of {c.Capacity.Value:0} L)";

        // the container a thing is, or is part of
        private static Container In(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var found = Of(t.gameObject);
                if (found != null) return found;
            }
            return null;
        }

        // Where a hose end tied at point p on collider c goes: on c if it is (part of) a container; else, if c is
        // (part of) the thing a container sits directly in, on that container at its top (the nearest to p, with
        // more than one); else where it was tied. 'Part of' goes up from c to the thing's root (its body, or the
        // saved object it is), no further.
        public static KeyValuePair<Transform, Vector3> Snap(Collider c, Vector3 p)
        {
            if (In(c.transform) != null) return new KeyValuePair<Transform, Vector3>(c.transform, p);
            Container best = null;
            float bestD = float.MaxValue;
            for (var t = c.transform; t != null; t = t.parent)
            {
                foreach (Transform child in t)
                {
                    var found = Of(child.gameObject);
                    if (found == null) continue;
                    float d = Vector3.Distance(Top(found), p);
                    if (d < bestD) { bestD = d; best = found; }
                }
                if (t.GetComponent<Rigidbody>() != null || t.GetComponent<ES3Internal.ES3Prefab>() != null) break;
            }
            if (best == null) return new KeyValuePair<Transform, Vector3>(c.transform, p);
            Plugin.Verbose("hose end onto " + best.Go.name + " (" + best.Tag + ") in " + best.Go.transform.parent.name + ", from " + c.name);
            return new KeyValuePair<Transform, Vector3>(best.Go.transform, Top(best));
        }

        // the top middle of a container: of its colliders and what draws it, else where it is
        private static Vector3 Top(Container c)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var x in c.Go.GetComponents<Collider>()) { if (!any) { b = x.bounds; any = true; } else b.Encapsulate(x.bounds); }
            foreach (var x in c.Go.GetComponents<Renderer>()) { if (!any) { b = x.bounds; any = true; } else b.Encapsulate(x.bounds); }
            return any ? new Vector3(b.center.x, b.max.y, b.center.z) : c.Go.transform.position;
        }

        // a liquid container: the game's LiquidAmount FSM, and what it holds
        private static Container Of(GameObject go)
        {
            if (go == null) return null;
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmName != "LiquidAmount") continue;
                var amount = fsm.FsmVariables.FindFsmFloat("Liquid");
                var capacity = fsm.FsmVariables.FindFsmFloat("LiquidCapacity");
                string liquid = Holds(go);
                if (amount == null || capacity == null || liquid == null) return null;
                var shape = go.GetComponents<Collider>();
                var solid = shape.Where(x => !x.isTrigger).ToArray();
                return new Container { Go = go, Amount = amount, Capacity = capacity, Tag = liquid, Shape = solid.Length > 0 ? solid : shape };
            }
            return null;
        }

        // what a container holds: its tag, or, untagged, what its own pouring (UseCan) pours into
        private static string Holds(GameObject go)
        {
            if (!go.CompareTag("Untagged")) return go.tag;
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmName != "UseCan") continue;
                foreach (var state in fsm.FsmStates)
                    foreach (var action in state.Actions)
                        if (action is HutongGames.PlayMaker.Actions.TriggerEvent te && te.collideTag != null && !string.IsNullOrEmpty(te.collideTag.Value) && te.collideTag.Value != "Untagged")
                            return te.collideTag.Value;
            }
            return null;
        }

        // what the hose is doing, for the kit's instructions
        public string Status
        {
            get
            {
                if (_a == null || _b == null || !_a.Valid || !_b.Valid)
                    return (_a != null && _a.Valid) || (_b != null && _b.Valid) ? "One end isn't in a container." : "Neither end is in a container.";
                if (_a.Go == _b.Go) return "Both ends are in the same container.";
                if (_a.Tag != _b.Tag) return $"{_a.Tag} at one end, {_b.Tag} at the other: no flow.";
                if (Mathf.Abs(_moved) < Quiet) return _flow != 0f ? $"{_a.Tag}, not flowing: one end is full or the other empty." : $"{_a.Tag}, not flowing.";
                return Mathf.Abs(_moved) < Mathf.Abs(_flow) * 0.9f
                    ? $"{_a.Tag}, {Mathf.Abs(_moved) * 1000f:0} ml/s (could carry {Mathf.Abs(_flow):0.00} L/s; the other end takes no more)."
                    : $"{_a.Tag}, {Mathf.Abs(_moved):0.00} L/s.";
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _flow = 0f;
            if (dt > 0f && _a != null && _b != null && _a.Valid && _b.Valid && _a.Go != _b.Go && _a.Tag == _b.Tag)
            {
                // each way: the source's surface over the outlet, which is where the hose ends or, once the
                // receiving container is filled past that, its surface
                Vector3 endA = _rope.EndA, endB = _rope.EndB;
                float sa = _a.Surface, sb = _b.Surface;
                float headAB = sa - Mathf.Max(endB.y, sb), headBA = sb - Mathf.Max(endA.y, sa);
                if (headAB > MinHead && _a.Amount.Value > 0f && _b.Amount.Value < _b.Capacity.Value) _flow = Rate(headAB);
                else if (headBA > MinHead && _b.Amount.Value > 0f && _a.Amount.Value < _a.Capacity.Value) _flow = -Rate(headBA);
                float moved = _flow != 0f ? Mathf.Sign(_flow) * Move(_flow > 0f ? _a : _b, _flow > 0f ? _b : _a, Mathf.Abs(_flow) * dt) : 0f;
                _moved += (moved / dt - _moved) * (1f - Mathf.Exp(-dt / Smoothing));
                Log(sa, sb, endA.y, endB.y);
            }
            else
            {
                _moved = 0f;
                Log(float.NaN, float.NaN, float.NaN, float.NaN);
            }
            Sound();
        }

        // what it is doing, logged when that changes, and every 10 s while it flows
        private void Log(float sa, float sb, float ya, float yb)
        {
            if (!Plugin.VerboseLogEntry.Value) return;
            bool flowing = Mathf.Abs(_moved) >= Quiet;
            string doing = flowing ? (_moved > 0f ? "flowing a to b" : "flowing b to a") : _flow != 0f ? "full or empty" : Status;
            if (doing == _logged && (!flowing || Time.time < _logAt)) return;
            _logged = doing;
            _logAt = Time.time + 10f;
            string levels = float.IsNaN(sa) ? "" : $"; surfaces a {sa:0.00} m, b {sb:0.00} m, ends a {ya:0.00} m, b {yb:0.00} m; a {_a.Amount.Value:0.0}/{_a.Capacity.Value:0} L, b {_b.Amount.Value:0.0}/{_b.Capacity.Value:0} L";
            Plugin.Verbose($"{_kind.Name} {Describe(_a)} - {Describe(_b)}: {doing}{(_flow != 0f ? $" at {Mathf.Abs(_moved):0.000} L/s (could {Mathf.Abs(_flow):0.000})" : "")}{levels}");
        }

        // litres from one container to the other, no more than is there or fits
        private static float Move(Container from, Container to, float litres)
        {
            litres = Mathf.Min(litres, from.Amount.Value, to.Capacity.Value - to.Amount.Value);
            if (litres <= 0f) return 0f;
            from.Amount.Value -= litres;
            to.Amount.Value += litres;
            return litres;
        }

        // L/s through the hose for a head h (m): the head goes into the velocity head, the inlet loss and friction,
        // 2 g h = v^2 (1 + K + f L / D). Laminar (f = 64 / Re) that is a quadratic in v; turbulent, f by Blasius,
        // iterated from the laminar answer.
        private float Rate(float h)
        {
            float d = _kind.Bore, length = Mathf.Max(0.1f, _rope.Length);
            float nu = RopeKinds.Liquid(_a.Tag).Viscosity;
            float a = 1f + InletLoss, b = 64f * nu * length / (d * d), c = 2f * 9.81f * h;
            float v = (-b + Mathf.Sqrt(b * b + 4f * a * c)) / (2f * a);
            if (v * d / nu > 2300f)
                for (int i = 0; i < 6; i++)
                {
                    float f = 0.316f / Mathf.Pow(Mathf.Max(2300f, v * d / nu), 0.25f);
                    v = Mathf.Sqrt(c / (a + f * length / d));
                }
            return v * Mathf.PI * d * d / 4f * 1000f;
        }

        // The game's pouring sound for this liquid, looped while it flows, as loud as what it carries (full at the
        // game's own pouring rate; a container topped up a few millilitres at a time, for a plant or a radiator, is
        // barely heard) and as the outlet is near: a 2D source faded by distance by hand (the game's 3D sources go
        // quiet at some camera angles).
        private static AudioListener _listener;

        private void Sound()
        {
            float carried = Mathf.Abs(_moved);
            if (carried < Quiet)
            {
                if (_audio != null && _audio.isPlaying) _audio.Stop();
                return;
            }
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.loop = true;
                _audio.spatialBlend = 0f;
                _audio.playOnAwake = false;
            }
            var clip = Clip(RopeKinds.Liquid(_a.Tag).Sound);
            if (_audio.clip != clip) { _audio.Stop(); _audio.clip = clip; }
            if (_listener == null || !_listener.isActiveAndEnabled) _listener = FindObjectOfType<AudioListener>();
            Vector3 outlet = _moved > 0f ? _rope.EndB : _rope.EndA;
            float near = _listener != null ? 1f - Mathf.Clamp01(Vector3.Distance(_listener.transform.position, outlet) / Hearing) : 1f;
            _audio.volume = near * Mathf.Clamp01(carried / FullFlow);
            if (clip != null && !_audio.isPlaying) _audio.Play();
        }

        private static AudioClip Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // a sound found missing stays missing: looking means going through every loaded clip
            if (!Clips.TryGetValue(name, out var clip))
            {
                clip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(x => x.name == name);
                if (clip == null) Plugin.Log.LogWarning("hose: no sound '" + name + "'");
                Clips[name] = clip;
            }
            return clip;
        }
    }
}
