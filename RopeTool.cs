using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace BurnersUtilities
{
    // The rope kits, worked like the game's toolbox (the player camera's 'UseToolbox'): look at one within 2 m
    // with an empty hand and press Use to take its ropes out, Use on a kit again to put them away. A kit is the
    // game's toolbox under its own id (a SigmaMod variant) whose ID FSM names a section of the rope kit table
    // (RopeKinds), so the game's own toolbox logic leaves it alone, and the table says what it ties.
    //
    // With the ropes out the game's grabbing is off (its left click would pick the thing up instead of tying
    // to it), and the put-away key (X) puts them away as well as Use on the kit.
    //
    // With the ropes out: left click a point on anything, then a second point, and a rope is tied between them
    // at that length. Right click drops a rope you have started, or cuts the rope you aim at. The mouse wheel
    // aimed at a rope pays it out or takes it in. Holding something pauses it.
    internal sealed class RopeTool : MonoBehaviour
    {
        private const float KitReach = 2f;         // the game's toolbox pick distance
        private const float AimTolerance = 0.12f;   // how close the aim has to pass a rope to pick it, m
        private const float PayOut = 0.1f;          // per wheel notch, m

        private RopeKind _out;
        private Collider _startCollider;
        private Vector3 _startPoint;
        private LineRenderer _preview;
        private AudioClip _toolboxClip;
        private bool _clipLooked;
        private GUIStyle _style;

        private static GameObject Global(string name)
        {
            var g = FsmVariables.GlobalVariables.FindFsmGameObject(name);
            return g != null ? g.Value : null;
        }

        private Camera Eye()
        {
            var pc = Global("PlayerCamera");
            var cam = pc != null ? pc.GetComponent<Camera>() : null;
            return cam != null && cam.isActiveAndEnabled ? cam : Camera.main;
        }

        private static bool HandBusy()
        {
            var use = Global("HandItemUse");
            var hand = Global("Hand");
            return (use != null && use.transform.childCount > 0) || (hand != null && hand.transform.childCount > 0);
        }

        private void Update()
        {
            if (!Plugin.RopesEntry.Value || Global("Player") == null)
            {
                if (_out != null) PutAway();
                return;
            }
            var cam = Eye();
            if (cam == null || Cursor.lockState != CursorLockMode.Locked) { HidePreview(); return; }
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

            RopeKind kit;
            if (Input.GetButtonDown("Use") && (kit = AimedAtKit(ray)) != null)
            {
                if (_out != null) PutAway();
                else if (!HandBusy()) TakeOut(kit);
                return;
            }
            if (_out == null) return;
            if (Input.GetKeyDown(Plugin.PutAwayKeyEntry.Value)) { PutAway(); return; }
            HoldGrab();
            if (HandBusy()) { HidePreview(); return; }

            float reach = Plugin.ReachEntry.Value;
            RaycastHit hit;
            bool aimed = Pick(ray, reach, out hit);

            if (Input.GetMouseButtonDown(0) && aimed)
            {
                if (_startCollider == null)
                {
                    _startCollider = hit.collider;
                    _startPoint = hit.point;
                }
                else
                {
                    if (_startCollider != null && Vector3.Distance(_startPoint, hit.point) > 0.05f)
                        Rope.Tie(_out, _startCollider, _startPoint, hit.collider, hit.point, 0f);
                    _startCollider = null;
                }
            }
            else if (Input.GetMouseButtonDown(1))
            {
                if (_startCollider != null) _startCollider = null;
                else
                {
                    var r = Rope.Aimed(ray, reach, AimTolerance);
                    if (r != null) r.Cut();
                }
            }
            else if (_startCollider == null)
            {
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.001f)
                {
                    var r = Rope.Aimed(ray, reach, AimTolerance);
                    if (r != null) r.Length += Mathf.Sign(wheel) * PayOut;
                }
            }

            // the end you are holding, to where you aim
            if (_startCollider != null)
            {
                ShowPreview(_startPoint, aimed ? hit.point : ray.origin + ray.direction * reach);
            }
            else HidePreview();
        }

        // what the aim hits within reach, not the player, not triggers
        private static bool Pick(Ray ray, float reach, out RaycastHit hit)
        {
            var player = Global("Player");
            var hits = Physics.RaycastAll(ray, reach, ~0, QueryTriggerInteraction.Ignore);
            hit = default(RaycastHit);
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in hits)
            {
                if (h.collider == null || (player != null && h.collider.transform.IsChildOf(player.transform))) continue;
                if (h.distance < best) { best = h.distance; hit = h; found = true; }
            }
            return found;
        }

        // the rope kit under the aim (the game's toolbox pick: 2 m, layers 0 and 9), by its ID
        private static RopeKind AimedAtKit(Ray ray)
        {
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, KitReach, (1 << 0) | (1 << 9))) return null;
            foreach (var fsm in hit.collider.GetComponentsInParent<PlayMakerFSM>())
            {
                if (fsm.FsmName != "ID") continue;
                var id = fsm.FsmVariables.GetFsmString("ID");
                var kind = id != null ? RopeKinds.Find(id.Value) : null;
                if (kind != null) return kind;
            }
            return null;
        }

        // The player camera's 'GrabItem' picks up whatever loose thing a left click lands on. With the ropes out
        // the hands are empty, so it is only ever waiting ('idle' / 'over'): it goes back to waiting (which
        // clears its "grab" prompt) and is switched off, and on again when the ropes are put away.
        private const string GrabFsm = "GrabItem";
        private static readonly string[] Waiting = { "idle", "over" };
        private PlayMakerFSM _grab;

        private PlayMakerFSM Grab()
        {
            if (_grab == null)
            {
                var pc = Global("PlayerCamera");
                _grab = pc != null ? pc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == GrabFsm) : null;
            }
            return _grab;
        }

        private void HoldGrab()
        {
            var g = Grab();
            if (g == null || !g.enabled || !Waiting.Contains(g.ActiveStateName)) return;
            if (g.ActiveStateName != "idle") g.SetState("idle");
            g.enabled = false;
        }

        private void ReleaseGrab()
        {
            var g = Grab();
            if (g != null && !g.enabled) g.enabled = true;
        }

        private void TakeOut(RopeKind kind)
        {
            _out = kind;
            _startCollider = null;
            HoldGrab();
            Clack();
            Plugin.Verbose(kind.Name + " out");
        }

        private void PutAway()
        {
            _out = null;
            _startCollider = null;
            HidePreview();
            ReleaseGrab();
            Clack();
            Plugin.Verbose("ropes away");
        }

        // the game's toolbox sound, as its own toolbox plays it
        private void Clack()
        {
            if (!_clipLooked)
            {
                _clipLooked = true;
                _toolboxClip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c.name == "toolbox");
            }
            var cam = Eye();
            if (_toolboxClip != null && cam != null) AudioSource.PlayClipAtPoint(_toolboxClip, cam.transform.position, 0.3f);
        }

        private void ShowPreview(Vector3 from, Vector3 to)
        {
            if (_preview == null)
            {
                var go = new GameObject("Rope preview");
                go.hideFlags = HideFlags.HideAndDontSave;
                _preview = go.AddComponent<LineRenderer>();
                _preview.useWorldSpace = true;
                _preview.positionCount = 2;
                _preview.textureMode = LineTextureMode.Tile;
            }
            _preview.sharedMaterial = _out.Material();
            _preview.startWidth = _preview.endWidth = _out.Thickness;
            _preview.enabled = true;
            _preview.SetPosition(0, from);
            _preview.SetPosition(1, to);
        }

        private void HidePreview()
        {
            if (_preview != null) _preview.enabled = false;
        }

        private void OnGUI()
        {
            if (_out == null || Cursor.lockState != CursorLockMode.Locked) return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(0.75f, 1f, 0.75f) } };
            string title = _out.Name.ToUpperInvariant() + "\n";
            string text = HandBusy()
                ? title + "Your hands are full."
                : _startCollider == null
                    ? title + "Left click: tie one from here\nRight click: cut the one you aim at\nMouse wheel on one: pay out / take in\n" + Plugin.PutAwayKeyEntry.Value + " (or Use on the kit): put it away"
                    : title + "Left click: tie the other end here\nRight click: drop this one";
            GUI.Label(new Rect(20f, 20f, 600f, 160f), text, _style);
        }
    }
}
