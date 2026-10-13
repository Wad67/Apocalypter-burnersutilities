using System.Collections.Generic;
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
    // With the ropes out: left click a point on anything, middle click any points it should run over (as over a
    // pulley), then left click the other end, and a rope is tied through them at that length. Right click drops a
    // rope you have started, or cuts the rope you aim at. The mouse wheel aimed at a rope pays it out or takes it
    // in. Holding something pauses it. A hose's ends go into the container in the thing clicked (Hose.Snap).
    internal sealed class RopeTool : MonoBehaviour
    {
        private const float KitReach = 2f;         // the game's toolbox pick distance
        private const float AimTolerance = 0.12f;   // how close the aim has to pass a rope to pick it, m
        private const float PayOut = 0.1f;          // per wheel notch, m

        private RopeKind _out;
        private static RopeTool _instance;
        public static bool Out => _instance != null && _instance._out != null;
        private void Awake() { _instance = this; }
        private readonly List<KeyValuePair<Transform, Vector3>> _placed = new List<KeyValuePair<Transform, Vector3>>();
        private LineRenderer _preview;
        private AudioClip _toolboxClip;
        private bool _clipLooked;
        private GUIStyle _style;
        private string _hoseStatus;

        private static GameObject Global(string name) => Hands.Global(name);
        private static Camera Eye() => Hands.Eye();
        private static bool HandBusy() => Hands.Busy();

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
            _hoseStatus = null;
            if (HandBusy()) { HidePreview(); return; }

            float reach = Plugin.ReachEntry.Value;
            RaycastHit hit;
            bool aimed = Pick(ray, reach, out hit);

            // a placed point dropped when its thing is gone
            if (_placed.Any(x => x.Key == null)) _placed.Clear();

            if (Input.GetMouseButtonDown(0) && aimed)
            {
                var end = At(hit, true);
                if (_placed.Count == 0) _placed.Add(end);
                else
                {
                    if (Vector3.Distance(_placed[_placed.Count - 1].Value, end.Value) > 0.05f || _placed.Count > 1)
                    {
                        _placed.Add(end);
                        Rope.Tie(_out, _placed.ToArray(), 0f);
                    }
                    _placed.Clear();
                }
            }
            else if (Input.GetMouseButtonDown(2) && aimed && _placed.Count > 0)
            {
                var over = At(hit, false);
                if (Vector3.Distance(_placed[_placed.Count - 1].Value, over.Value) > 0.05f) _placed.Add(over);
            }
            else if (Input.GetMouseButtonDown(1))
            {
                if (_placed.Count > 0) _placed.Clear();
                else
                {
                    var r = Rope.Aimed(ray, reach, AimTolerance);
                    if (r != null) r.CutBy("cut");
                }
            }
            else if (_placed.Count == 0)
            {
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.001f)
                {
                    var r = Rope.Aimed(ray, reach, AimTolerance);
                    if (r != null) r.Length += Mathf.Sign(wheel) * PayOut;
                }
            }

            // what the hose under the aim is doing
            var aimedHose = _placed.Count == 0 ? Rope.Aimed(ray, reach, AimTolerance) : null;
            var hose = aimedHose != null ? aimedHose.GetComponent<Hose>() : null;
            _hoseStatus = hose != null ? hose.Status : null;

            // the end you are holding, to where you aim
            if (_placed.Count > 0)
            {
                Vector3 to = aimed ? At(hit, true).Value : ray.origin + ray.direction * reach;
                ShowPreview(to);
            }
            else HidePreview();
        }

        // a point where the aim hits: on the collider's own object; a hose's end goes into a container there
        private KeyValuePair<Transform, Vector3> At(RaycastHit hit, bool end)
        {
            if (end && _out != null && _out.IsHose) return Hose.Snap(hit.collider, hit.point);
            return new KeyValuePair<Transform, Vector3>(hit.collider.transform, hit.point);
        }

        private static bool Pick(Ray ray, float reach, out RaycastHit hit) => Hands.Pick(ray, reach, out hit);

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

        // the game's grab is off while the ropes are out (its left click would pick the thing up)
        private void HoldGrab() => Hands.HoldGrab(this);
        private void ReleaseGrab() => Hands.ReleaseGrab(this);

        public void PutAwayIfOut()
        {
            if (_out != null) PutAway();
        }

        private void TakeOut(RopeKind kind)
        {
            _out = kind;
            _placed.Clear();
            HoldGrab();
            Clack();
            Plugin.Verbose(kind.Name + " out");
        }

        private void PutAway()
        {
            _out = null;
            _placed.Clear();
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

        private void ShowPreview(Vector3 to)
        {
            if (_preview == null)
            {
                var go = new GameObject("Rope preview");
                go.hideFlags = HideFlags.HideAndDontSave;
                _preview = go.AddComponent<LineRenderer>();
                _preview.useWorldSpace = true;
                _preview.textureMode = LineTextureMode.Tile;
            }
            _preview.sharedMaterial = _out.Material();
            _preview.startWidth = _preview.endWidth = _out.Thickness;
            _preview.enabled = true;
            _preview.positionCount = _placed.Count + 1;
            for (int i = 0; i < _placed.Count; i++)
                _preview.SetPosition(i, _placed[i].Value);
            _preview.SetPosition(_placed.Count, to);
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
                : _placed.Count == 0
                    ? title + "Left click: tie one from here\nRight click: cut the one you aim at\nMouse wheel on one: pay out / take in\n" + Plugin.PutAwayKeyEntry.Value + " (or Use on the kit): put it away"
                    : title + "Left click: tie the other end here\nMiddle click: run it over this point\nRight click: drop this one";
            if (_hoseStatus != null && !HandBusy()) text += "\n\n" + _hoseStatus;
            GUI.Label(new Rect(20f, 20f, 600f, 200f), text, _style);
        }
    }
}
