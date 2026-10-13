using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BurnersUtilities
{
    // What each rope kit ties: strength, thickness and look, from BepInEx/config/BurnersUtilities.ropes.cfg (one
    // section per kit, by the kit's ID). Shipped as an embedded default; a file in an older format is kept as .old
    // and replaced. Re-read when the file changes.
    internal sealed class RopeKind
    {
        public string Id, Name, Texture;
        public float Strength = 30f;      // kN
        public float Thickness = 0.012f;  // m
        public int RowFrom, RowTo;        // texture rows of one strand, from the top
        public float Metallic, Smoothness = 0.1f;
        public Color Color = new Color(0.36f, 0.29f, 0.19f);
        public float Bore;                 // m, inside diameter; above zero: a hose
        public float Elongation = 0.1f;    // its stretch at breaking strength, of its length

        public bool IsHose => Bore > 0f;

        private Material _material;

        // the game texture's strand, repeated along the rope at the strand's own proportions
        public Material Material()
        {
            if (_material != null) return _material;
            var shader = Shader.Find("Standard");
            _material = new Material(shader != null ? shader : Shader.Find("Diffuse")) { name = "rope " + Id };
            if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", Metallic);
            if (_material.HasProperty("_Glossiness")) _material.SetFloat("_Glossiness", Smoothness);
            var tex = string.IsNullOrEmpty(Texture) ? null : Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t.name == Texture);
            int rows = RowTo - RowFrom;
            if (tex == null || rows <= 0)
            {
                if (!string.IsNullOrEmpty(Texture)) Plugin.Log.LogWarning($"rope kit {Id}: no texture '{Texture}' (or no band); drawn plain");
                _material.color = Color;
                return _material;
            }
            _material.mainTexture = tex;
            tex.wrapMode = TextureWrapMode.Repeat;
            // across: the band only. Along (LineRenderer Tile: u in metres): one repeat per strand length at this
            // thickness, the band being `rows` pixels across and the texture `width` pixels along.
            float repeat = Thickness * tex.width / rows;
            _material.mainTextureScale = new Vector2(1f / repeat, (float)rows / tex.height);
            _material.mainTextureOffset = new Vector2(0f, 1f - (float)RowTo / tex.height);
            return _material;
        }
    }

    // What a hose carries, by the tag the game gives a container of it ([liquid: <tag>] sections)
    internal sealed class LiquidKind
    {
        public string Tag;
        public float Viscosity = 1e-6f;            // m2/s
        public string Sound = "liquid_filling2";
    }

    internal static class RopeKinds
    {
        private const string Header = "# Burner's Utilities rope kits (format 3).";
        private const string Resource = "BurnersUtilities.ropes.default.cfg";

        private static string _path;
        private static DateTime _stamp;
        private static Dictionary<string, RopeKind> _kinds = new Dictionary<string, RopeKind>();
        private static Dictionary<string, LiquidKind> _liquids = new Dictionary<string, LiquidKind>();
        private static readonly LiquidKind Unlisted = new LiquidKind { Tag = "?" };

        public static void Init(string configDir)
        {
            _path = Path.Combine(configDir, "BurnersUtilities.ropes.cfg");
            try
            {
                if (File.Exists(_path))
                {
                    if (File.ReadAllText(_path).StartsWith(Header)) return;
                    File.Copy(_path, _path + ".old", true);
                    Plugin.Log.LogWarning(Path.GetFileName(_path) + " was in an older format; kept as " + Path.GetFileName(_path) + ".old and replaced with the defaults");
                }
                using (var s = typeof(RopeKinds).Assembly.GetManifestResourceStream(Resource))
                using (var r = new StreamReader(s))
                    File.WriteAllText(_path, r.ReadToEnd());
            }
            catch (Exception e) { Plugin.Log.LogError("rope kits: " + e.Message); }
        }

        // the kind a kit ID ties, null for an ID that isn't a rope kit
        public static RopeKind Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Reload();
            return _kinds.TryGetValue(id, out var k) ? k : null;
        }

        // a liquid by its container tag; one not listed runs like water
        public static LiquidKind Liquid(string tag)
        {
            Reload();
            return tag != null && _liquids.TryGetValue(tag, out var l) ? l : Unlisted;
        }

        private static void Reload()
        {
            if (_path == null || !File.Exists(_path)) return;
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _stamp) return;
            _stamp = stamp;
            var kinds = new Dictionary<string, RopeKind>();
            var liquids = new Dictionary<string, LiquidKind>();
            RopeKind k = null;
            LiquidKind liquid = null;
            int n = 0;
            foreach (var raw in File.ReadAllLines(_path))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string id = line.Substring(1, line.Length - 2).Trim();
                    if (id.StartsWith("liquid:"))
                    {
                        string tag = id.Substring(7).Trim();
                        liquids[tag] = liquid = new LiquidKind { Tag = tag };
                        k = null;
                    }
                    else
                    {
                        kinds[id] = k = new RopeKind { Id = id, Name = id };
                        liquid = null;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if ((k == null && liquid == null) || eq < 0) { Plugin.Log.LogWarning($"{Path.GetFileName(_path)} line {n}: expected [kit id], [liquid: tag] or key = value"); continue; }
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                try
                {
                    if (liquid != null)
                    {
                        switch (key)
                        {
                            case "viscosity": liquid.Viscosity = Num(val, 1e-8f, 1f); break;
                            case "sound": liquid.Sound = val; break;
                            default: throw new FormatException("unknown key '" + key + "'");
                        }
                        continue;
                    }
                    switch (key)
                    {
                        case "name": k.Name = val; break;
                        case "breaking_strength": k.Strength = Num(val, 0.1f, 10000f); break;
                        case "thickness": k.Thickness = Num(val, 0.001f, 0.2f); break;
                        case "texture": k.Texture = val; break;
                        case "band":
                            var p = val.Split(',');
                            if (p.Length != 2) throw new FormatException("expected from, to");
                            k.RowFrom = (int)Num(p[0], 0, 16384); k.RowTo = (int)Num(p[1], 0, 16384);
                            if (k.RowTo <= k.RowFrom) throw new FormatException("'to' must be below 'from'");
                            break;
                        case "metallic": k.Metallic = Num(val, 0, 1); break;
                        case "smoothness": k.Smoothness = Num(val, 0, 1); break;
                        case "color":
                            var c = val.Split(',');
                            if (c.Length != 3) throw new FormatException("expected r, g, b");
                            k.Color = new Color(Num(c[0], 0, 1), Num(c[1], 0, 1), Num(c[2], 0, 1));
                            break;
                        case "bore": k.Bore = Num(val, 0f, 0.2f); break;
                        case "stretch": k.Elongation = Num(val, 0.1f, 500f) / 100f; break;
                        default: throw new FormatException("unknown key '" + key + "'");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"{Path.GetFileName(_path)} line {n}: {e.Message}"); }
            }
            _kinds = kinds;
            _liquids = liquids;
            Plugin.Verbose("rope kits: " + string.Join(", ", kinds.Values.Select(x => $"{x.Id} ({x.Name}, {x.Strength} kN, {x.Thickness * 1000f:0} mm{(x.IsHose ? $", {x.Bore * 1000f:0} mm bore hose" : "")})").ToArray()));
        }

        private static float Num(string s, float min, float max)
        {
            float v = float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (v < min || v > max) throw new FormatException($"{s.Trim()} is outside {min} - {max}");
            return v;
        }
    }
}
