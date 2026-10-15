using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // Standing on something that moves. The player's 'Movement' FSM sets the player's horizontal velocity
    // every frame and physics step from the keys alone (SetVelocity x, z; y left to gravity), so whatever the
    // player stands on is ignored: a moving vehicle slides out from under them. After that action has set it,
    // the velocity of the body under the player's feet at the point they stand on is added (a turning vehicle
    // carries them round with it). Off the ground they keep what they had when they left it, as they would;
    // landing on something that doesn't move takes it away again.
    [HarmonyPatch(typeof(SetVelocity), "DoSetVelocity")]
    internal static class PlatformRide
    {
        private static GameObject _player;
        private static Rigidbody _body;
        private static CapsuleCollider _capsule;
        private static Vector3 _carry;

        // the velocity added for what the player stands on (or left with), world space
        internal static Vector3 Carry { get { return _carry; } }
        internal static GameObject Player { get { return _player; } }
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        // never let a failure here stop the game's movement FSM (the rest of its actions would not run)
        private static void Postfix(SetVelocity __instance)
        {
            try { Ride(__instance); }
            catch (System.Exception e) { Plugin.Verbose("platform ride: " + e.Message); }
        }

        private static void Ride(SetVelocity action)
        {
            if (!Plugin.RideAlongEntry.Value) return;
            var target = action.Fsm != null ? action.Fsm.GetOwnerDefaultTarget(action.gameObject) : null;
            if (target == null || !IsPlayer(target)) return;
            var body = _body;
            if (body == null || body.isKinematic) return;
            _carry = Ground(body, out bool grounded, out Rigidbody under) ?? _carry;
            if (grounded && under == null) _carry = Vector3.zero;
            if (_carry.sqrMagnitude < 1e-6f) return;
            var v = body.velocity;
            v.x += _carry.x;
            v.z += _carry.z;
            body.velocity = v;
        }

        // The player and their body, looked up again whenever what was found is gone: getting into a vehicle and
        // out again takes the player's Rigidbody away and gives them a new one
        private static bool IsPlayer(GameObject go)
        {
            if (_player == null)
            {
                var g = FsmVariables.GlobalVariables.FindFsmGameObject("Player");
                _player = g != null ? g.Value : null;
                _body = null;
                _capsule = null;
                _carry = Vector3.zero;
            }
            if (_player != null && _body == null) _body = _player.GetComponent<Rigidbody>();
            if (_player != null && _capsule == null) _capsule = _player.GetComponent<CapsuleCollider>();
            return go != null && go == _player;
        }

        // whether the player is standing on something (for HandHold: their feet's grip)
        // What holds the player up (for HandHold: what their feet push against): seated, the vehicle they sit in
        // (a body the player is part of, theirs taken away or held still); standing, what is under their feet (null
        // for the ground). False in the air.
        internal static bool Support(out Rigidbody under, out Vector3 feet, out bool seated)
        {
            IsPlayer(null);
            under = null;
            feet = _player != null ? _player.transform.position : Vector3.zero;
            seated = false;
            if (_player == null) return true;
            // the body the player is part of: the vehicle, the outermost body above them (a seat can carry a small body
            // of its own, nested in the vehicle's; pushed, that moves the seat, and the player in it, not the vehicle),
            // never their own camera rig
            Rigidbody parentBody = null;
            for (var t = _player.transform.parent; t != null; t = t.parent)
            {
                var rb = t.GetComponent<Rigidbody>();
                if (rb != null && !Hands.PlayerRig(rb.transform)) parentBody = rb;
            }
            if (_body == null || _body.isKinematic || parentBody != null)
            {
                seated = true;
                under = parentBody;
                return true;
            }
            var v = Ground(_body, out bool grounded, out under);
            if (_capsule != null) feet = new Vector3(_capsule.bounds.center.x, _capsule.bounds.min.y, _capsule.bounds.center.z);
            return grounded;
        }

        internal static Rigidbody PlayerBody()
        {
            IsPlayer(null);
            return _body;
        }

        // what the player stands on: its velocity at their feet (null in the air); under is null for static ground
        private static Vector3? Ground(Rigidbody body, out bool grounded, out Rigidbody under)
        {
            grounded = false;
            under = null;
            if (_capsule == null) return null;
            var b = _capsule.bounds;
            float r = Mathf.Min(b.extents.x, b.extents.z) * 0.9f;
            float reach = b.extents.y - r + 0.15f;
            int n = Physics.SphereCastNonAlloc(b.center, r, Vector3.down, Hits, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            int pick = -1;
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.collider == null || h.collider.attachedRigidbody == body || h.distance <= 0f) continue;
                if (h.distance < best) { best = h.distance; pick = i; }
            }
            if (pick < 0) return null;
            grounded = true;
            under = Hits[pick].collider.attachedRigidbody;
            if (under == null) return Vector3.zero;
            return under.GetPointVelocity(Hits[pick].point);
        }
    }

    // The player's footstep sound and head bob go by how far the player moved since the last frame
    // (GetGameObjectSpeed). Carried on a vehicle that includes the vehicle's travel, so standing still
    // sounded and looked like walking. For the player, the distance carried this frame comes out first,
    // leaving the player's own steps.
    [HarmonyPatch(typeof(GetGameObjectSpeed), "doComputeSpeed")]
    internal static class CarriedSteps
    {
        private static readonly AccessTools.FieldRef<GetGameObjectSpeed, GameObject> Go =
            AccessTools.FieldRefAccess<GetGameObjectSpeed, GameObject>("go");
        private static readonly AccessTools.FieldRef<GetGameObjectSpeed, Vector3> LastPosition =
            AccessTools.FieldRefAccess<GetGameObjectSpeed, Vector3>("lastPosition");

        private static void Prefix(GetGameObjectSpeed __instance)
        {
            try
            {
                if (!Plugin.RideAlongEntry.Value) return;
                var go = Go(__instance);
                if (go == null || go != PlatformRide.Player) return;
                var carry = PlatformRide.Carry;
                carry.y = 0f;
                if (carry.sqrMagnitude < 1e-6f) return;
                LastPosition(__instance) += carry * Time.deltaTime;
            }
            catch (System.Exception e) { Plugin.Verbose("carried steps: " + e.Message); }
        }
    }
}
