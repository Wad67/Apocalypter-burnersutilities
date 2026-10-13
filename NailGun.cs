using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace BurnersUtilities
{
    // The nail gun: the game's .22 pipe revolver under its own id (a SigmaMod variant, BurnersUtilities/sigma_assets/
    // nail_gun.json), equipped like the revolver (in a weapon slot, with the revolver's arms and model), and known
    // by its own NailGun FSM, which also holds its nails: NailLength (m), NailDiameter (m), NailShear (kN).
    //
    // While it is equipped the revolver's own shooting, reloading, ammo and aiming FSMs on its first-person model
    // are off, and the game's grab too (its left click would pick the thing up). Left click fires a nail where the
    // aim meets something within Reach: it goes in along the aim (NailBoard), with the revolver's kick and the
    // hammer's clack. Right click pulls out the nail whose head the aim passes nearest.
    internal sealed class NailGun : MonoBehaviour
    {
        private const string GunFsm = "NailGun";
        private const float Reach = 2f;            // m, the gun against the work
        private const float MinBite = 0.005f;      // m a nail has to go into what is behind to hold it
        private const float AimTolerance = 0.04f;  // how near the aim has to pass a nail's head to pull it, m
        private static readonly string[] Held = { "Attack", "Reload", "Chambered", "AmmoUI", "AimDownSights" };

        private PlayMakerFSM[] _slots;         // the weapon slot FSMs (WeaponInHand), found once
        private PlayMakerFSM _equippedGun;     // the item's NailGun FSM, while it is equipped
        private static NailGun _instance;
        public static bool IsOut => _instance != null && _instance._equippedGun != null;
        private void Awake() { _instance = this; }
        private GameObject _model;             // the revolver's first-person model
        private PlayMakerFSM[] _heldOff = new PlayMakerFSM[0];
        private string _last;
        private float _lastUntil;
        private GUIStyle _style;
        private Material _steel;

        private void Update()
        {
            GameObject model = null;
            var gun = Plugin.NailsEntry.Value ? Equipped(out model) : null;
            if (gun != _equippedGun) Switch(gun, gun != null ? model : null);
            if (_equippedGun == null || Cursor.lockState != CursorLockMode.Locked) return;
            Hands.HoldGrab(this);
            var cam = Hands.Eye();
            if (cam == null) return;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Input.GetMouseButtonDown(0)) Fire(ray);
            else if (Input.GetMouseButtonDown(1))
            {
                var n = NailBoard.Aimed(ray, Reach, AimTolerance);
                if (n != null)
                {
                    Vector3 at = n.Head;
                    NailBoard.Remove(n);
                    Sounds.Play("wrench1", at, 0.5f);
                    Say("Nail pulled out.");
                }
            }
        }

        // the equipped weapon, if it is a nail gun: the weapon slot FSM (WeaponInHand) has an item whose NailGun FSM
        // is there; its first-person model is what the slot FSM's current state switched on
        private PlayMakerFSM Equipped(out GameObject model)
        {
            model = null;
            if (_slots == null || _slots.Any(x => x == null))
            {
                var cam = Hands.Global("PlayerCamera");
                if (cam == null) return null;
                _slots = cam.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "WeaponInHand").ToArray();
            }
            foreach (var slot in _slots)
            {
                var item = slot.FsmVariables.FindFsmGameObject("Item");
                if (item == null || item.Value == null) continue;
                var gun = item.Value.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == GunFsm);
                if (gun == null) continue;
                var state = slot.Fsm.ActiveState;
                if (state == null || state.Actions == null) continue;
                foreach (var a in state.Actions)
                    if (a is ActivateGameObject act && act.activate != null && act.activate.Value)
                        model = slot.Fsm.GetOwnerDefaultTarget(act.gameObject);
                if (model == null) continue;   // not out (switching, or put away)
                return gun;
            }
            return null;
        }

        // the revolver's shooting held off while the nail gun is out, and back on after
        private void Switch(PlayMakerFSM gun, GameObject model)
        {
            foreach (var f in _heldOff) if (f != null) f.enabled = true;
            _heldOff = new PlayMakerFSM[0];
            if (_equippedGun != null) Hands.ReleaseGrab(this);
            _equippedGun = gun;
            _model = model;
            if (gun == null) { Plugin.Verbose("nail gun away"); return; }
            GetComponent<RopeTool>()?.PutAwayIfOut();
            _heldOff = model.GetComponents<PlayMakerFSM>().Where(f => Held.Contains(f.FsmName) && f.enabled).ToArray();
            foreach (var f in _heldOff) f.enabled = false;
            Plugin.Verbose($"nail gun out: {Length * 1000f:0} x {Diameter * 1000f:0.0} mm nails, {Shear / 1000f:0.0} kN; held off on {model.name}: "
                + string.Join(", ", _heldOff.Select(f => f.FsmName).ToArray()));
        }

        private float Value(string name, float fallback)
        {
            var v = _equippedGun != null ? _equippedGun.FsmVariables.FindFsmFloat(name) : null;
            return v != null && v.Value > 0f ? v.Value : fallback;
        }

        private float Length => Value("NailLength", 0.1f);
        private float Diameter => Value("NailDiameter", 0.006f);
        private float Shear => Value("NailShear", 9f) * 1000f;

        private void Fire(Ray ray)
        {
            if (!Hands.Pick(ray, Reach, out var hit)) { Say("Nothing there to nail."); return; }
            Kick();
            Vector3 d = ray.direction, head = hit.point;
            float length = Length;
            var a = hit.collider;
            var bodyA = a.attachedRigidbody;
            var nail = new Nail { A = a.transform, Shear = Shear };
            nail.HeadA = a.transform.InverseTransformPoint(head);
            nail.DirA = a.transform.InverseTransformDirection(d);

            // through the first thing: where it comes out (back along the nail from its tip)
            Vector3 tip = head + d * length;
            Collider b = null;
            Vector3 into = Vector3.zero;
            if (a.Raycast(new Ray(tip, -d), out var back, length))
            {
                Vector3 exit = back.point;
                float left = length - Vector3.Distance(head, exit);
                // and into what is behind, as far as the rest reaches (from just inside the first, to find a thing
                // pressed against it)
                var player = Hands.Global("Player");
                float best = float.MaxValue;
                foreach (var h in Physics.RaycastAll(new Ray(exit - d * 0.002f, d), left + 0.002f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.collider == null || h.collider == a) continue;
                    if (bodyA != null && h.collider.attachedRigidbody == bodyA) continue;
                    if (player != null && h.collider.transform.IsChildOf(player.transform)) continue;
                    if (h.distance - 0.002f > left - MinBite) continue;
                    if (h.distance < best) { best = h.distance; b = h.collider; into = h.point; }
                }
            }
            if (b != null)
            {
                nail.B = b.transform;
                nail.HasB = true;
                nail.PivotA = a.transform.InverseTransformPoint(into);
                nail.PivotB = b.transform.InverseTransformPoint(into);
            }
            else nail.PivotA = nail.HeadA;
            nail.Shown = Show(a.transform, head, d, length);
            NailBoard.Add(nail);
            Sounds.Play("knife_hit", head, 0.5f);
            Say(NailBoard.Describe(nail));
            Plugin.Verbose("nail fired into " + a.name + (b != null ? " and on into " + b.name + " (" + (b.attachedRigidbody != null ? b.attachedRigidbody.name : "the world") + ")" : ", nothing behind"));
        }

        // the revolver's own kick and the hammer's clack
        private void Kick()
        {
            if (_model != null)
                foreach (var f in _model.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "Recoil1") f.SendEvent("Activate");
            var cam = Hands.Eye();
            if (cam != null) Sounds.Play("bolt_action_2", cam.transform.position, 0.6f);
        }

        // the nail as it looks: a steel shank along the nail and a head flat on the surface, on the thing it went into
        private GameObject Show(Transform on, Vector3 head, Vector3 d, float length)
        {
            if (_steel == null)
            {
                _steel = new Material(Shader.Find("Standard")) { name = "nail steel", color = new Color(0.55f, 0.56f, 0.58f) };
                _steel.SetFloat("_Metallic", 0.8f);
                _steel.SetFloat("_Glossiness", 0.45f);
            }
            var root = new GameObject("Nail");
            root.transform.SetParent(on, true);
            root.transform.position = head;
            root.transform.rotation = Quaternion.FromToRotation(Vector3.up, d);
            float dia = Diameter;
            Part(root.transform, new Vector3(0f, length * 0.5f, 0f), new Vector3(dia, length * 0.5f, dia));
            Part(root.transform, new Vector3(0f, -0.0012f, 0f), new Vector3(dia * 2.6f, 0.0012f, dia * 2.6f));
            return root;
        }

        private void Part(Transform root, Vector3 local, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root, false);
            go.transform.localPosition = local;
            // its size in the world, whatever the scale of what it is on
            Vector3 lossy = root.lossyScale;
            go.transform.localScale = new Vector3(size.x / Mathf.Max(1e-4f, lossy.x), size.y / Mathf.Max(1e-4f, lossy.y), size.z / Mathf.Max(1e-4f, lossy.z));
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _steel;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void Say(string text)
        {
            _last = text;
            _lastUntil = Time.time + 4f;
        }

        private void OnGUI()
        {
            if (_equippedGun == null || Cursor.lockState != CursorLockMode.Locked) return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(0.75f, 1f, 0.75f) } };
            string text = "NAIL GUN\nLeft click: fire a nail\nRight click: pull out the nail you aim at";
            if (_last != null && Time.time < _lastUntil) text += "\n\n" + _last;
            GUI.Label(new Rect(20f, 20f, 600f, 160f), text, _style);
        }
    }
}
