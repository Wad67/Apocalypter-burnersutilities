using System.Collections;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // Holding with the hands, by force, and being pulled by what you hold.
    //
    // Items: the game's grab (the player camera's 'GrabItem' FSM) parents the grabbed thing to the 'Hand' and puts it
    // where it is held (itemPos, itemRot, in the Hand's space) every frame, its velocity zeroed: nothing it is roped,
    // nailed or jointed to can hold it back, and they fight. Here the grab still does everything else (picking, its
    // prompts and text, the events it sends the thing, scrolling it nearer or further, turning it, dropping and
    // throwing it, and the thing stays under the Hand, so the game knows it is held), but the thing is held by its
    // own body, and turned to where the grab would turn it.
    //
    // Anything else with a body that moves (a car, a trailer, a loose part that isn't an item): the game won't grab
    // it, so here: look at it within GrabReach with empty hands and left click, and the point you looked at is held,
    // at that distance in front of you (scroll: nearer or further); left click again lets go. It isn't turned: it
    // turns about your grip as it will.
    //
    // The hold, either way, is an arm: a spring (Stiffness, N/m) and a damper between the thing and where it is
    // held, the damper against the hand's own motion, so it carries along with the hand and eases into place. The
    // same spring on any mass: a light thing follows almost at once, a heavy one lags and swings into place. The
    // spring is worked implicitly, so a stiff hold on a light thing is stable; at the grip of a big body it acts on
    // the body's mass there (its turning inertia counts). Stretched past the arm's reach (ArmReach) the arm is
    // straight: the hold goes stiff (Rigid), and what pulls the thing pulls you. No more force than the hands'
    // Strength; asked for more than that for StrainTime, the hands let go.
    //
    // Heaving: keep the left button held after the click that took hold, and the hands go past their strength, up
    // to HeaveStrength, at a price: everything used past Strength hurts you (StrainDamage health a second for each
    // kN over), straight off your health (no armour against a hernia), and you grunt and groan, more often and
    // louder the harder it is. Let the button go and you are back to your strength; still too heavy, you drop it.
    //
    // You: every pull the hands make pulls back on you, an 80 kg body standing on your feet. While your feet's grip
    // holds (Grip times your weight) you stand; past it you are dragged, sliding, the grip braking you, until the
    // pull eases (off the ground nothing brakes you). The drag is added to the velocity the game's movement gives
    // you, after it sets it.
    //
    // The game makes a held item's first collider a trigger (and switches off its detailed 'colliders'), so it
    // passes through everything, the ground too. Held by force, that collider is solid again if it was before: it
    // rests on and slides along the ground and anything else without a body, but still passes through anything with
    // one (vehicles, so parts can go in; other things; the player): those solid colliders are found around it each
    // step and kept apart from it (triggers aren't, so the vehicle's fitting triggers still see it).
    //
    // An item under the Hand would be dragged with the camera every frame; it is put back where its body is,
    // smoothed between physics steps (its own interpolation, since Unity's would follow the Hand). A thing taken out
    // of a load bed has no body (the game gives it one when it is dropped): it gets one for holding, as heavy as the
    // game would make it (its LockPhysics FSM's mass).
    [DefaultExecutionOrder(-1000)]
    internal sealed class HandHold : MonoBehaviour
    {
        private const string GrabFsm = "GrabItem";
        private const int ItemLayer = 9;          // what the game's own grab picks up
        private static readonly string[] Holding = { "ItemInHand", "Rotate", "Forward", "Backward" };
        private const float Damping = 0.7f;       // damping ratio of the hold, for the thing's mass
        private const float HandSmoothing = 0.05f; // s: the hand's motion is averaged over about this long
        private const float TwistArm = 0.3f;      // m: the hands' reach about a held thing, for twisting it
        private const float ArmReach = 0.6f;      // m: how far the arms give before they are straight
        private const float Rigid = 20000f;       // N/m: a straight arm
        private const float StrainTime = 0.25f;   // s at full strength before the hands let go
        private const float GrabReach = 2f;       // m, as the game's grab
        private const float PlayerMass = 80f;     // kg
        private const float Grip = 0.75f;         // the feet's friction, of the player's weight
        private const float Heaviest = 100000f;   // kg: how heavy a thing held fast feels

        public static HandHold Instance;
        public static bool On => Plugin.HandHoldEntry != null && Plugin.HandHoldEntry.Value;

        private enum Kind { None, Item, Body }

        private PlayMakerFSM _grab;
        private Kind _kind;
        private GameObject _item;         // the item, or the body's object
        private Rigidbody _rb;
        private Vector3 _grip;            // a body's grip point, in its own space
        private float _distance;          // how far in front of the eye a body is held
        private bool _addedBody, _wasKinematic;
        private RigidbodyInterpolation _interpolation;
        private Vector3 _prevPos, _curPos;
        private Quaternion _prevRot, _curRot;
        private bool _havePose;
        private Vector3 _lastTarget;
        private Quaternion _lastTargetRot;
        private bool _haveTarget;
        private float _targetTime;        // when the hand was last measured
        private Vector3 _handVelocity, _handSpin;
        private Quaternion _target;       // where an item is turned to, in the world (the grab's Rotate turns this)
        private float _strained;
        private Vector3 _drag;            // the player being dragged, horizontal
        private bool _heaving;            // the button still held since taking hold
        private float _hurtOwed, _nextGrunt, _strainLogged;
        private bool _standing = true, _seated;     // what holds the player up this step
        private Rigidbody _under;
        private Vector3 _feet;
        private Vector3 _setVelocity;     // the player's velocity as the drag left it this step
        private float _apparent;          // how heavy the held thing feels at the grip (a tethered thing: very)
        private Vector3 _vBefore, _pulled, _pullDir;
        private bool _measure;
        private bool _dragSet;
        private PlayMakerFSM _health;
        private AudioSource _voice;
        private UnityEngine.UI.Text _prompt;
        private Collider _solid;          // a held item's collider, solid again
        private readonly System.Collections.Generic.HashSet<Collider> _apart = new System.Collections.Generic.HashSet<Collider>();
        private static readonly Collider[] Near = new Collider[128];
        private static readonly System.Collections.Generic.Dictionary<Collider, bool> WasTrigger = new System.Collections.Generic.Dictionary<Collider, bool>();

        // the grab making an item's collider a trigger: what it was before
        public static void Triggering(Collider c)
        {
            if (c != null && !WasTrigger.ContainsKey(c)) WasTrigger[c] = c.isTrigger;
        }
        private bool _prompting;

        public static Vector3 Drag => Instance != null ? Instance._drag : Vector3.zero;

        private void Awake()
        {
            Instance = this;
            Application.onBeforeRender += Show;
            StartCoroutine(AfterPhysics());
        }

        private void OnDestroy() { Application.onBeforeRender -= Show; }

        private PlayMakerFSM Grab()
        {
            if (_grab == null)
            {
                var pc = Hands.Global("PlayerCamera");
                _grab = pc != null ? pc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == GrabFsm) : null;
            }
            return _grab;
        }

        // the item the game's grab is holding now, if any
        private GameObject HeldItem()
        {
            var g = Grab();
            if (g == null || !On || !Holding.Contains(g.ActiveStateName)) return null;
            var item = g.FsmVariables.FindFsmGameObject("Item");
            return item != null ? item.Value : null;
        }

        public static bool IsHeld(GameObject go) => Instance != null && go != null && Instance._kind == Kind.Item && Instance._item == go;

        // ---- bodies the game doesn't grab: looking at one, taking hold, scrolling, letting go

        private void Update()
        {
            if (_heaving && !Input.GetMouseButton(0)) { _heaving = false; Plugin.Verbose("heaving stopped"); }
            if (_kind == Kind.Body)
            {
                if (_rb == null || !On || Hands.Busy() || Cursor.lockState != CursorLockMode.Locked || Input.GetMouseButtonDown(0)) { StopBody(); return; }
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.001f) _distance = Mathf.Clamp(_distance + Mathf.Sign(wheel) * 0.1f, 0.5f, ViewBehind() + GrabReach + 0.5f);
                return;
            }
            Rigidbody aimed = null;
            RaycastHit hit = default(RaycastHit);
            var g = Grab();
            if (On && _kind == Kind.None && g != null && g.enabled && g.ActiveStateName == "idle" && !Hands.Busy() && !RopeTool.Out && !NailGun.IsOut
                && Cursor.lockState == CursorLockMode.Locked)
            {
                var cam = Hands.View();
                var ray = cam != null ? Hands.Aim(cam) : default(Ray);
                if (cam != null && Hands.Pick(ray, GrabReach, out hit))
                {
                    var rb = hit.collider.attachedRigidbody;
                    if (rb != null && !rb.isKinematic && rb.gameObject.layer != ItemLayer && hit.collider.gameObject.layer != ItemLayer && rb != PlatformRide.PlayerBody()
                        && !Interaction(ray, hit))
                        aimed = rb;
                }
            }
            Prompt(aimed != null ? "grab: lmb" : null);
            if (aimed != null && Input.GetMouseButtonDown(0)) BeginBody(aimed, hit);
        }

        // Something the game does with the click (or Use) there: a thing the aim meets before the body, or the body's
        // own collider, triggers included, that has an FSM listening for the mouse or the game's use and hover
        // events, or a script taking clicks the Unity way (OnMouseDown: another mod's hand crank). The click is left to it.
        private static readonly System.Collections.Generic.Dictionary<int, bool> Listens = new System.Collections.Generic.Dictionary<int, bool>();
        private static readonly string[] UseEvents = { "UseItem", "OverItem", "useDoor", "Use" };

        private static bool Interaction(Ray ray, RaycastHit solid)
        {
            foreach (var h in Physics.RaycastAll(ray, solid.distance + 0.01f, ~0, QueryTriggerInteraction.Collide))
                if (h.collider != null && Listening(h.collider.gameObject)) return true;
            return false;
        }

        private static bool Listening(GameObject go)
        {
            int id = go.GetInstanceID();
            if (Listens.TryGetValue(id, out bool known)) return known;
            bool listens = false;
            // a script on it, or on a part it belongs to, that takes the click itself
            for (var t = go.transform; t != null && !listens; t = t.parent)
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) != null) { listens = true; break; }
                if (t.GetComponent<Rigidbody>() != null) break;   // up to the body it is part of
            }
            if (!listens)
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmGlobalTransitions.Any(t => t.FsmEvent != null && UseEvents.Contains(t.FsmEvent.Name))) { listens = true; break; }
                foreach (var state in fsm.FsmStates)
                {
                    if (state.Actions.Any(a => a is MousePickEvent || a is MousePick || a is GetMouseButtonDown || a is GetMouseButtonUp || a is GetMouseButton))
                    { listens = true; break; }
                }
                if (listens) break;
            }
            Listens[id] = listens;
            return listens;
        }

        private void BeginBody(Rigidbody rb, RaycastHit hit)
        {
            _kind = Kind.Body;
            _rb = rb;
            _item = rb.gameObject;
            _grip = rb.transform.InverseTransformPoint(hit.point);
            _distance = Mathf.Max(0.5f, hit.distance);
            _haveTarget = false;
            _handVelocity = _handSpin = Vector3.zero;
            _strained = 0f;
            _apparent = 0f;
            _heaving = Input.GetMouseButton(0);
            Hands.HoldGrab(this);
            Prompt("Release: lmb\nMove: scroll");
            var view = Hands.View(); var eye = Hands.Eye();
            bool standing = PlatformRide.Support(out var under, out _, out bool seated);
            Plugin.Verbose($"holding {rb.name} ({rb.mass:0} kg) by the hand at {hit.distance:0.00} m from the view ({(view != null ? view.name : "none")}" +
                           $"{(view != eye && eye != null ? ", the eye is " + eye.name : "")}): {Path(hit.collider.transform)}; player {(seated ? "seated" : standing ? "standing" : "in the air")}" +
                           $" on {(under != null ? Path(under.transform) + " (" + under.mass.ToString("0") + " kg)" : "nothing that moves")}");
        }

        private static string Path(Transform t) { var p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }

        private void StopBody()
        {
            if (_kind != Kind.Body) return;
            Plugin.Verbose("let go of " + (_item != null ? _item.name : "?"));
            _kind = Kind.None;
            _rb = null;
            _item = null;
            Hands.ReleaseGrab(this);
            Prompt(null);
        }

        // how far behind the player's eye the view is (a third-person view): the aim reaches that much further
        private static float ViewBehind()
        {
            var view = Hands.View(); var eye = Hands.Eye();
            return view != null && eye != null ? Vector3.Distance(view.transform.position, eye.transform.position) : 0f;
        }

        // the game's own grab prompt (the 'ItemGrab' text its idle state clears), for bodies
        private void Prompt(string text)
        {
            if (text == null && !_prompting) return;
            if (_prompt == null)
            {
                var g = Grab();
                var idle = g != null ? g.FsmStates.FirstOrDefault(st => st.Name == "idle") : null;
                var set = idle != null ? idle.Actions.OfType<UiTextSetText>().FirstOrDefault() : null;
                var go = set != null ? g.Fsm.GetOwnerDefaultTarget(set.gameObject) : null;
                _prompt = go != null ? go.GetComponent<UnityEngine.UI.Text>() : null;
                if (_prompt == null) return;
            }
            _prompt.text = text ?? "";
            _prompting = text != null;
        }

        // ---- the hold

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (_kind != Kind.Body)
            {
                var now = HeldItem();
                if (now != (_kind == Kind.Item ? _item : null)) { StopItem(); if (now != null) BeginItem(now); }
            }
            Vector3 pull = Vector3.zero;   // what the hands pull the thing with this step, N
            bool busy = _kind != Kind.None && _rb != null && _haveTarget;
            if (busy || _drag.sqrMagnitude > 1e-6f) _standing = PlatformRide.Support(out _under, out _feet, out _seated);
            if (busy) pull = Hold(dt);
            Feet(-pull, dt);
        }

        private Vector3 Hold(float dt)
        {
            if (_kind == Kind.Item && _havePose) _item.transform.SetPositionAndRotation(_curPos, _curRot);   // not where the camera dragged it
            if (_solid != null) KeepApart(dt);
            float k = Plugin.HandStiffnessEntry.Value, normal = Plugin.HandStrengthEntry.Value;
            float strength = _heaving ? Mathf.Max(normal, Plugin.HandHeaveEntry.Value) : normal;

            // where the hand is now: as last seen, carried on at its speed to this physics step (it moves per frame)
            float ahead = Mathf.Clamp(Time.fixedTime - _targetTime, 0f, 0.1f);
            Vector3 target = _lastTarget + _handVelocity * ahead;
            Vector3 grip = _kind == Kind.Item ? _item.transform.position : _rb.transform.TransformPoint(_grip);
            Vector3 off = target - grip;
            float d = off.magnitude;
            Vector3 n = d > 1e-5f ? off / d : Vector3.zero;

            // the arm: soft within its reach, straight past it (k off + Rigid (d - reach), as one spring)
            float kEff = k, offset = d;
            if (d > ArmReach)
            {
                kEff = k + Rigid;
                offset = d - Rigid * ArmReach / kEff;
            }
            // the thing's mass where it is held: an item at its centre, a body at the grip (turning counts)
            float m = _rb.mass;
            Vector3 r = grip - _rb.worldCenterOfMass;
            if (_kind == Kind.Body && n != Vector3.zero)
            {
                Vector3 rn = Vector3.Cross(r, n);
                m = 1f / (1f / _rb.mass + Vector3.Dot(rn, InverseInertia(_rb, rn)));
            }
            Vector3 v = _kind == Kind.Body ? _rb.GetPointVelocity(grip) : _rb.velocity;
            // Between the thing and the player: standing or seated, the player is held where they are, and the
            // spring works on the thing's mass; in the air or sliding they are free, and it works on the two of
            // them together (their reduced mass), so a thing held fast (tied to a structure) is a spring on the
            // player's own mass, damped for it
            bool free = !_seated && (!_standing || _drag.sqrMagnitude > 1e-4f);
            m = Mathf.Max(m, _apparent);
            float mu = free ? m * PlayerMass / (m + PlayerMass) : m;
            // the arm's damping is the arm's: for no more than the player's own mass
            float c = 2f * Damping * Mathf.Sqrt(kEff * Mathf.Min(mu, PlayerMass));
            Vector3 dv = dt * (kEff * offset * n + c * (_handVelocity - v)) / (mu + dt * c + dt * dt * kEff);
            Vector3 impulse = mu * dv;
            // an item is carried: the hands hold its weight up as well (a body held by a point isn't lifted: it
            // rests on whatever else holds it, and the arm's spring takes what it hangs on the hand)
            if (_kind == Kind.Item) impulse -= Physics.gravity * (_rb.mass * dt);
            float most = strength * dt;
            bool full = impulse.magnitude > most;
            if (full) impulse *= most / impulse.magnitude;
            _vBefore = v; _pulled = impulse; _pullDir = n; _measure = true;
            Strain(impulse.magnitude / dt - normal, dt);
            _rb.velocity += impulse / _rb.mass;
            if (_kind == Kind.Body) _rb.angularVelocity += InverseInertia(_rb, Vector3.Cross(r, impulse));
            if (_kind == Kind.Item) Turn(dt, k, strength);
            _rb.WakeUp();

            // more than the hands can hold, for long enough: let go
            _strained = full ? _strained + dt : 0f;
            if (_strained > StrainTime)
            {
                _strained = 0f;
                string why = $"pulled harder than the hands hold ({strength:0} N), {d:0.00} m from the hand";
                if (_kind == Kind.Item) Hands.LetGo(_item, why);
                else { Plugin.Verbose((_item != null ? _item.name : "?") + " pulled out of the hands: " + why); StopBody(); }
            }
            return impulse / dt;
        }

        // the held item's solid collider kept apart from everything with a body around it (not triggers)
        private void KeepApart(float dt)
        {
            var b = _solid.bounds;
            float pad = 0.5f + _rb.velocity.magnitude * dt * 2f;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * pad, Near, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = Near[i];
                if (c == null || c == _solid || c.isTrigger || _apart.Contains(c)) continue;
                var body = c.attachedRigidbody;
                if (body == null || body == _rb) continue;   // the ground and what has no body: it rests on that
                Physics.IgnoreCollision(_solid, c, true);
                _apart.Add(c);
            }
        }

        private void Unsolid()
        {
            if (_solid != null)
                foreach (var c in _apart)
                    if (c != null) Physics.IgnoreCollision(_solid, c, false);
            _apart.Clear();
            _solid = null;
        }

        // the player's own hurt sounds: what their DamageEffectSound FSM plays when they are hurt (the voice the
        // player has; a clip found by name could be another mod's, loaded or not)
        private static AudioClip Voice()
        {
            var player = Hands.Global("Player");
            if (player == null) return null;
            foreach (var fsm in player.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "DamageEffectSound") continue;
                foreach (var state in fsm.FsmStates)
                    foreach (var a in state.Actions.OfType<PlayRandomSound>())
                    {
                        var clips = a.audioClips?.Select(x => x?.Value as AudioClip).Where(x => x != null).ToArray();
                        if (clips != null && clips.Length > 0) return clips[Random.Range(0, clips.Length)];
                    }
            }
            return null;
        }

        // what heaving past the hands' strength costs (excess force, N): health, and the voice
        private void Strain(float excess, float dt)
        {
            if (excess <= 0f) return;
            _hurtOwed += Plugin.HandStrainDamageEntry.Value * excess / 1000f * dt;
            if (_hurtOwed >= 0.05f)
            {
                if (_health == null)
                {
                    var player = Hands.Global("Player");
                    _health = player != null ? player.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health") : null;
                }
                var hp = _health != null ? _health.FsmVariables.FindFsmFloat("Health") : null;
                if (hp != null) hp.Value -= _hurtOwed;
                _hurtOwed = 0f;
            }
            // a grunt or a groan, sooner and louder the harder it is (full at HeaveStrength)
            float effort = Mathf.Clamp01(excess / Mathf.Max(1f, Plugin.HandHeaveEntry.Value - Plugin.HandStrengthEntry.Value));
            if (Time.time >= _nextGrunt)
            {
                _nextGrunt = Time.time + Mathf.Lerp(2.5f, 0.8f, effort) * Random.Range(0.8f, 1.2f);
                var clip = Voice();
                if (clip != null)
                {
                    if (_voice == null) { _voice = gameObject.AddComponent<AudioSource>(); _voice.spatialBlend = 0f; _voice.playOnAwake = false; }
                    _voice.pitch = Random.Range(0.85f, 1.0f);
                    _voice.PlayOneShot(clip, Mathf.Lerp(0.25f, 0.9f, effort));
                }
            }
            if (Time.time >= _strainLogged)
            {
                _strainLogged = Time.time + 2f;
                Plugin.Verbose($"heaving {(_item != null ? _item.name : "?")}: {excess:0} N past the hands' strength, {Plugin.HandStrainDamageEntry.Value * excess / 1000f:0.0} health a second");
            }
        }

        // an item turned to where the grab holds it: the same spring at a hand's reach, about each of its principal
        // axes on its inertia there
        private void Turn(float dt, float k, float strength)
        {
            (_target * Quaternion.Inverse(_rb.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            Vector3 error = !float.IsNaN(axis.x) && Mathf.Abs(angle) > 1e-3f ? axis * (angle * Mathf.Deg2Rad) : Vector3.zero;
            Quaternion q = _rb.rotation * _rb.inertiaTensorRotation, back = Quaternion.Inverse(q);
            Vector3 e = back * error, w = back * _rb.angularVelocity, wh = back * _handSpin, inertia = _rb.inertiaTensor, dwl = Vector3.zero;
            float kr = k * TwistArm * TwistArm;
            for (int i = 0; i < 3; i++)
            {
                float ii = Mathf.Max(1e-5f, inertia[i]);
                float cr = 2f * Damping * Mathf.Sqrt(kr * ii);
                dwl[i] = dt * (kr * e[i] + cr * (wh[i] - w[i])) / (ii + dt * cr + dt * dt * kr);
            }
            Vector3 twist = Vector3.Scale(inertia, dwl);
            float most = strength * TwistArm * dt;
            if (twist.magnitude > most) dwl *= most / twist.magnitude;
            _rb.angularVelocity += q * dwl;
        }

        // The player, pulled by what the hands hold (force on them, N), and what holds the player up taking it.
        // Seated, it all goes into the vehicle. On their feet: pushed down, or pulled up less than their weight, they
        // stay on their feet and what they stand on takes it (the bed of a sprung trailer sinks under what they
        // hold); pulled up harder than their weight, they leave the ground, an 80 kg body. Sideways, the feet hold up
        // to Grip times their weight, and all of it goes into what is under them; past that they slide, dragged,
        // braked by their grip, and only that friction goes into what is under them. In the air nothing holds them.
        // So pulling on the thing you sit or stand on pulls it against itself: it may rock, it doesn't go anywhere.
        private void Feet(Vector3 force, float dt)
        {
            if (force.sqrMagnitude < 1e-4f && _drag.sqrMagnitude < 1e-6f) { _drag = Vector3.zero; return; }
            if (_seated) { _drag = Vector3.zero; Push(_under, force, _feet, dt); return; }
            float weight = PlayerMass * 9.81f;
            var body = PlatformRide.PlayerBody();
            bool lifted = !_standing || force.y > weight;
            if (lifted)
            {
                // off the ground (or pulled off it): the pull moves the 80 kg player; their weight is the game's gravity
                if (body != null && !body.isKinematic) { body.velocity += Vector3.up * (force.y * dt / PlayerMass); body.WakeUp(); }
            }
            else Push(_under, Vector3.up * force.y, _feet, dt);
            Vector3 flat = force;
            flat.y = 0f;
            if (!_standing) { _drag += flat / PlayerMass * dt; return; }
            float hold = Grip * Mathf.Max(0f, weight - force.y);   // the feet grip as hard as they are pressed down
            if (_drag.sqrMagnitude < 1e-4f && flat.magnitude <= hold) { _drag = Vector3.zero; Push(_under, flat, _feet, dt); return; }
            Vector3 sliding = _drag.sqrMagnitude > 1e-6f ? _drag.normalized : flat.normalized;
            Vector3 friction = -sliding * hold;
            Vector3 next = _drag + (flat + friction) / PlayerMass * dt;
            if (Vector3.Dot(next, _drag) < 0f && flat.magnitude <= hold) next = Vector3.zero;   // braked to a stop
            _drag = next;
            Push(_under, -friction, _feet, dt);
        }

        // the velocity the player was given with the drag this step (after the game's movement set it)
        public static void DragGiven(Vector3 v)
        {
            if (Instance == null) return;
            Instance._setVelocity = v;
            Instance._dragSet = true;
        }

        // a force on a body for a step, at a point
        private static void Push(Rigidbody rb, Vector3 force, Vector3 at, float dt)
        {
            if (rb == null || rb.isKinematic || force.sqrMagnitude < 1e-6f) return;
            Vector3 impulse = force * dt;
            rb.velocity += impulse / rb.mass;
            rb.angularVelocity += InverseInertia(rb, Vector3.Cross(at - rb.worldCenterOfMass, impulse));
            rb.WakeUp();
        }

        // a body's world inverse inertia applied to v
        private static Vector3 InverseInertia(Rigidbody rb, Vector3 v)
        {
            Quaternion q = rb.rotation * rb.inertiaTensorRotation;
            Vector3 local = Quaternion.Inverse(q) * v, inertia = rb.inertiaTensor;
            local.x = inertia.x > 0f ? local.x / inertia.x : 0f;
            local.y = inertia.y > 0f ? local.y / inertia.y : 0f;
            local.z = inertia.z > 0f ? local.z / inertia.z : 0f;
            return q * local;
        }

        private void BeginItem(GameObject item)
        {
            _kind = Kind.Item;
            _item = item;
            _rb = item.GetComponent<Rigidbody>();
            _addedBody = false;
            if (_rb == null)
            {
                // out of a load bed: a body as the game would give it on dropping it
                _rb = item.AddComponent<Rigidbody>();
                _addedBody = true;
                var lockFsm = item.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "LockPhysics");
                var mass = lockFsm != null ? lockFsm.FsmVariables.FindFsmFloat("mass") : null;
                if (mass != null && mass.Value > 0f) _rb.mass = mass.Value;
            }
            _wasKinematic = _rb.isKinematic;
            _rb.isKinematic = false;
            _rb.useGravity = true;   // it keeps its weight; the hands carry it (the game takes gravity away)
            // its collider solid again, if the grab made it a trigger and it wasn't one
            var first = item.GetComponent<Collider>();
            _apart.Clear();
            _solid = null;
            if (first != null && first.isTrigger && WasTrigger.TryGetValue(first, out bool was) && !was)
            {
                first.isTrigger = false;
                _solid = first;
                KeepApart(Time.fixedDeltaTime);   // before it can touch the player or a vehicle
            }
            WasTrigger.Clear();
            _interpolation = _rb.interpolation;
            _rb.interpolation = RigidbodyInterpolation.None;
            _curPos = _prevPos = _rb.position;
            _curRot = _prevRot = _rb.rotation;
            _target = _rb.rotation;
            _havePose = true;
            _haveTarget = false;
            _handVelocity = _handSpin = Vector3.zero;
            _strained = 0f;
            _apparent = 0f;
            _heaving = Input.GetMouseButton(0);
            var me = PlatformRide.PlayerBody();
            Plugin.Verbose($"holding {item.name} by force ({_rb.mass:0.#} kg{(_addedBody ? ", given a body" : "")}); the player's body is {(me != null ? me.mass.ToString("0.#") + " kg" : "missing")}");
        }

        private void StopItem()
        {
            if (_kind != Kind.Item) return;
            // still in the hand (held the game's way from now on): a trigger again, as the grab made it
            var keep = _solid;
            Unsolid();
            var hand = Hands.Global("Hand");
            if (keep != null && hand != null && keep.transform.IsChildOf(hand.transform)) keep.isTrigger = true;
            if (_item != null && _rb != null)
            {
                _rb.interpolation = _interpolation;
                if (_wasKinematic) _rb.isKinematic = true;
                Plugin.Verbose("let go of " + _item.name);
            }
            _kind = Kind.None;
            _item = null;
            _rb = null;
            _havePose = false;
        }

        // after each physics step: where a held item's body went
        private IEnumerator AfterPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                // what stopped the player (a wall, the thing they hang from) stops the drag too: what the contacts
                // took off the velocity they were given comes off the drag, so it doesn't build up while they don't move
                if (_dragSet)
                {
                    _dragSet = false;
                    var body = PlatformRide.PlayerBody();
                    if (body != null && _drag.sqrMagnitude > 1e-6f)
                    {
                        Vector3 lost = body.velocity - _setVelocity;
                        lost.y = 0f;
                        Vector3 along = _drag.normalized;
                        float took = Vector3.Dot(lost, along);
                        if (took < 0f) _drag += along * Mathf.Max(took, -_drag.magnitude);
                    }
                }
                // how heavy the held thing turned out to be, at the grip, along the pull: what the pull should have
                // done to its speed against what it did (gravity taken out); held fast, it barely moves
                if (_measure && _rb != null && _kind != Kind.None)
                {
                    _measure = false;
                    float jn = Vector3.Dot(_pulled, _pullDir);
                    if (Mathf.Abs(jn) > 0.02f)
                    {
                        Vector3 grip = _kind == Kind.Body ? _rb.transform.TransformPoint(_grip) : _rb.worldCenterOfMass;
                        Vector3 after = _kind == Kind.Body ? _rb.GetPointVelocity(grip) : _rb.velocity;
                        Vector3 change = after - _vBefore - (_rb.useGravity ? Physics.gravity * Time.fixedDeltaTime : Vector3.zero);
                        float along = Vector3.Dot(change, _pullDir);
                        float seen = along * jn > 0f ? jn / along : Heaviest;
                        seen = Mathf.Clamp(Mathf.Abs(seen), 0.1f, Heaviest);
                        _apparent = _apparent <= 0f ? seen : Mathf.Exp(Mathf.Lerp(Mathf.Log(_apparent), Mathf.Log(seen), 0.3f));
                    }
                }
                if (_kind != Kind.Item || _rb == null) continue;
                _prevPos = _curPos; _prevRot = _curRot;
                _curPos = _rb.position; _curRot = _rb.rotation;
            }
        }

        // before drawing: where the hand holds the thing this frame (and how the hand moved), and where a held item's
        // body is, between the last two physics steps
        private void Show()
        {
            if (_kind == Kind.None || _rb == null) return;
            float dt = Time.deltaTime;
            Vector3 target;
            Quaternion turn = _target;
            if (_kind == Kind.Item)
            {
                var hand = Hands.Global("Hand");
                var g = Grab();
                var pos = g != null ? g.FsmVariables.FindFsmVector3("itemPos") : null;
                if (hand == null || pos == null) return;
                target = hand.transform.TransformPoint(pos.Value);
                var rot = g.FsmVariables.FindFsmVector3("itemRot");
                if (g.ActiveStateName != "Rotate" && rot != null) turn = hand.transform.rotation * Quaternion.Euler(rot.Value);
                _target = turn;
            }
            else
            {
                // held on the aim: where the crosshair points, as far along it as it was taken
                var cam = Hands.View();
                if (cam == null) return;
                var aim = Hands.Aim(cam);
                target = aim.origin + aim.direction * _distance;
                turn = cam.transform.rotation;
            }
            if (dt > 0f)
            {
                if (_haveTarget)
                {
                    float smooth = 1f - Mathf.Exp(-dt / HandSmoothing);
                    _handVelocity = Vector3.Lerp(_handVelocity, (target - _lastTarget) / dt, smooth);
                    (turn * Quaternion.Inverse(_lastTargetRot)).ToAngleAxis(out float a, out Vector3 ax);
                    if (a > 180f) a -= 360f;
                    _handSpin = Vector3.Lerp(_handSpin, !float.IsNaN(ax.x) && Mathf.Abs(a) > 1e-3f ? ax * (a * Mathf.Deg2Rad / dt) : Vector3.zero, smooth);
                }
                _lastTarget = target; _lastTargetRot = turn; _haveTarget = true; _targetTime = Time.time;
            }
            if (_kind == Kind.Item && _havePose)
            {
                float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
                _item.transform.SetPositionAndRotation(Vector3.Lerp(_prevPos, _curPos, t), Quaternion.Slerp(_prevRot, _curRot, t));
            }
        }

        // The grab turning the held item (its Rotate state turns the thing's transform, then reads it back as
        // itemRot): the turn goes to where it is held instead, and itemRot is read from that
        public static bool Turn(GameObject go, Vector3 euler, Space space)
        {
            if (!IsHeld(go)) return false;
            var q = Quaternion.Euler(euler);
            Instance._target = space == Space.Self ? Instance._target * q : q * Instance._target;
            return true;
        }

        public static bool ReadTurn(GameObject go, FsmVector3 into)
        {
            if (!IsHeld(go) || into == null) return false;
            var hand = Hands.Global("Hand");
            if (hand == null) return false;
            into.Value = (Quaternion.Inverse(hand.transform.rotation) * Instance._target).eulerAngles;
            return true;
        }
    }

    // the player dragged by what the hands hold: added to the velocity the game's movement sets
    [HarmonyPatch(typeof(SetVelocity), "DoSetVelocity")]
    internal static class DraggedPlayer
    {
        private static void Postfix(SetVelocity __instance)
        {
            try
            {
                var drag = HandHold.Drag;
                if (drag.sqrMagnitude < 1e-6f || __instance.Fsm == null) return;
                var body = PlatformRide.PlayerBody();
                if (body == null || __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject) != body.gameObject) return;
                var v = body.velocity;
                v.x += drag.x;
                v.z += drag.z;
                body.velocity = v;
                HandHold.DragGiven(v);
            }
            catch (System.Exception e) { Plugin.Verbose("dragged player: " + e.Message); }
        }
    }

    // The grab's own placing of the held thing (position, rotation, velocity, spin), off while it is held by force;
    // on dropping and throwing it keeps the motion the hands gave it (the throw's push still adds to it)
    internal static class HandHoldPatches
    {
        private static bool Grabbing(FsmStateAction a, FsmOwnerDefault target, out GameObject go)
        {
            go = null;
            if (!HandHold.On || a.Fsm == null || a.Fsm.Name != "GrabItem") return false;
            go = a.Fsm.GetOwnerDefaultTarget(target);
            return go != null && (HandHold.IsHeld(go) || a.State != null && (a.State.Name == "Drop" || a.State.Name == "Throw"));
        }

        // the grab making the item's collider a trigger: remember what it was
        [HarmonyPatch(typeof(ColliderSetIsTrigger), "DoSetIsTrigger")]
        private static class RememberTrigger
        {
            private static void Prefix(ColliderSetIsTrigger __instance)
            {
                if (!HandHold.On || __instance.Fsm == null || __instance.Fsm.Name != "GrabItem" || __instance.isTrigger == null || !__instance.isTrigger.Value) return;
                var go = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (go != null) HandHold.Triggering(go.GetComponent<Collider>());
            }
        }

        [HarmonyPatch(typeof(SetPosition), "DoSetPosition")]
        private static class NoSetPosition
        {
            private static bool Prefix(SetPosition __instance) => !Grabbing(__instance, __instance.gameObject, out _);
        }

        [HarmonyPatch(typeof(SetRotation), "DoSetRotation")]
        private static class NoSetRotation
        {
            private static bool Prefix(SetRotation __instance) => !Grabbing(__instance, __instance.gameObject, out _);
        }

        [HarmonyPatch(typeof(SetVelocity), "DoSetVelocity")]
        private static class NoSetVelocity
        {
            private static bool Prefix(SetVelocity __instance) => !Grabbing(__instance, __instance.gameObject, out _);
        }

        [HarmonyPatch(typeof(SetAngularVelocity), "DoSetAngularVelocity")]
        private static class NoSetSpin
        {
            private static bool Prefix(SetAngularVelocity __instance) => !Grabbing(__instance, __instance.gameObject, out _);
        }

        [HarmonyPatch(typeof(Rotate), "DoRotate")]
        private static class TurnHeld
        {
            private static bool Prefix(Rotate __instance)
            {
                if (!Grabbing(__instance, __instance.gameObject, out var go) || !HandHold.IsHeld(go)) return true;
                var v = __instance.vector.IsNone ? Vector3.zero : __instance.vector.Value;
                if (__instance.perSecond) v *= Time.deltaTime;
                HandHold.Turn(go, v, __instance.space);
                return false;
            }
        }

        [HarmonyPatch(typeof(GetRotation), "DoGetRotation")]
        private static class ReadHeldTurn
        {
            private static bool Prefix(GetRotation __instance)
            {
                if (__instance.State == null || __instance.State.Name != "Rotate") return true;
                if (!Grabbing(__instance, __instance.gameObject, out var go) || !HandHold.IsHeld(go)) return true;
                return !HandHold.ReadTurn(go, __instance.vector);
            }
        }
    }
}
