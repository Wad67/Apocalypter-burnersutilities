using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Den.Tools;
using Den.Tools.Splines;
using HarmonyLib;
using MapMagic.Core;
using MapMagic.Products;
using MapMagic.Terrains;
using UnityEngine;

namespace BurnersUtilities
{
    // Terrain seam diagnostic (Debug.TerrainSeams). Every time MapMagic welds a tile's edge to a neighbour, it copies
    // the neighbour's edge row of heights over the tile's own. What it replaces is exactly how far the two tiles
    // disagree along that edge. This records, per welded edge: the largest disagreement in metres and where along the
    // edge, the mean, how much of the edge is off by more than 0.25 m, and where the tile's road splines cross that
    // edge. Steps that sit on road crossings point at road routing; steps spread along whole edges point at something
    // else in the height chain. Written to BepInEx/BurnersUtilities-seams.csv; edges off by a metre or more also go to
    // the log. Changes nothing in the terrain.
    internal static class SeamLog
    {
        private static bool Enabled => Plugin.TerrainSeamsEntry != null && Plugin.TerrainSeamsEntry.Value;

        // road splines of each generated tile, as world (x, z) segments, captured before MapMagic clears its products
        private static readonly Dictionary<string, List<Vector4>> Roads = new Dictionary<string, List<Vector4>>();
        private static readonly object Gate = new object();
        private static readonly FieldInfo ProductsField = AccessTools.Field(typeof(TileData), "products");
        private static string _file;

        private static string Key(Coord c, bool draft) => c.x + "," + c.z + (draft ? "d" : "m");

        public static void Init()
        {
            TerrainTile.OnTileFinalized += Capture;
        }

        private static void Capture(TerrainTile tile, TileData data, StopToken stop)
        {
            if (!Enabled || tile == null || data == null) return;
            try
            {
                var segs = new List<Vector4>();
                Gather(data, segs, 0);
                lock (Gate) Roads[Key(tile.coord, data.isDraft)] = segs;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("seam log: road capture failed: " + e.Message);
            }
        }

        private static void Gather(TileData data, List<Vector4> segs, int depth)
        {
            if (ProductsField?.GetValue(data) is Dictionary<ulong, object> products)
                foreach (var p in products.Values)
                    if (p is SplineSys sys && sys.lines != null)
                        foreach (var line in sys.lines)
                            if (line?.segments != null)
                                foreach (var s in line.segments)
                                    segs.Add(new Vector4(s.start.pos.x, s.start.pos.z, s.end.pos.x, s.end.pos.z));
            if (depth < 4 && data.subDatas != null)
                foreach (var sub in data.subDatas.Values)
                    if (sub != null) Gather(sub, segs, depth + 1);
        }

        [HarmonyPatch(typeof(Weld), nameof(Weld.WeldEdgesInThread))]
        private static class WeldPatch
        {
            private static void Prefix(EdgesSet thisEdges, out float[][] __state)
            {
                __state = null;
                if (!Enabled || thisEdges?.heightEdges == null) return;
                var e = thisEdges.heightEdges;
                __state = new[] { Copy(e.arr_x), Copy(e.arr_X), Copy(e.arr_z), Copy(e.arr_Z) };
            }

            private static void Postfix(EdgesSet thisEdges, TerrainTileManager tileManager, Coord coord, bool isDraft, float[][] __state)
            {
                if (__state == null || tileManager == null) return;
                try { Report(thisEdges.heightEdges, tileManager, coord, isDraft, __state); }
                catch (Exception e) { Plugin.Log.LogWarning("seam log: " + e.Message); }
            }
        }

        private static float[] Copy(float[] a) => a == null ? null : (float[])a.Clone();

        private static void Report(Edges after, TerrainTileManager tiles, Coord coord, bool draft, float[][] before)
        {
            var tile = tiles[coord];
            if (tile?.mapMagic == null) return;
            float height = tile.mapMagic.globals.height;
            float sizeX = tile.mapMagic.tileSize.x, sizeZ = tile.mapMagic.tileSize.z;
            float x0 = coord.x * sizeX, z0 = coord.z * sizeZ;
            List<Vector4> roads;
            lock (Gate) Roads.TryGetValue(Key(coord, draft), out roads);
            // (edge, neighbour, the array before and after, along x or z, the fixed coordinate)
            var edges = new (string name, Coord n, float[] was, float[] now, bool alongX, float fixedAt)[]
            {
                ("-z", new Coord(coord.x, coord.z - 1), before[0], after.arr_x, true, z0),
                ("+z", new Coord(coord.x, coord.z + 1), before[1], after.arr_X, true, z0 + sizeZ),
                ("-x", new Coord(coord.x - 1, coord.z), before[2], after.arr_z, false, x0),
                ("+x", new Coord(coord.x + 1, coord.z), before[3], after.arr_Z, false, x0 + sizeX),
            };
            foreach (var e in edges)
            {
                if (e.was == null || e.now == null || e.was.Length != e.now.Length || e.was.Length < 2) continue;
                int n = e.was.Length, over = 0, at = 0;
                float max = 0f, sum = 0f;
                for (int i = 0; i < n; i++)
                {
                    float d = Math.Abs(e.now[i] - e.was[i]) * height;
                    sum += d;
                    if (d > 0.25f) over++;
                    if (d > max) { max = d; at = i; }
                }
                if (sum == 0f) continue;   // no neighbour welded on this edge
                float t = (float)at / (n - 1);
                float atX = e.alongX ? x0 + t * sizeX : e.fixedAt, atZ = e.alongX ? e.fixedAt : z0 + t * sizeZ;
                // where the tile's roads cross this edge, and the nearest of those to the worst point
                var crossings = new List<float>();
                if (roads != null)
                    foreach (var s in roads)
                    {
                        float a = e.alongX ? s.y : s.x, b = e.alongX ? s.w : s.z;   // across the edge
                        if ((a - e.fixedAt) * (b - e.fixedAt) > 0f || a == b) continue;
                        float k = (e.fixedAt - a) / (b - a);
                        float along = e.alongX ? s.x + k * (s.z - s.x) : s.y + k * (s.w - s.y);
                        float lo = e.alongX ? x0 : z0, hi = lo + (e.alongX ? sizeX : sizeZ);
                        if (along >= lo && along <= hi) crossings.Add(along);
                    }
                float worstAlong = e.alongX ? atX : atZ, nearest = float.NaN;
                foreach (var c in crossings)
                    if (float.IsNaN(nearest) || Math.Abs(c - worstAlong) < nearest) nearest = Math.Abs(c - worstAlong);
                Write(coord, draft, e.name, e.n, max, atX, atZ, sum / n, (float)over / n, n, crossings, nearest);
            }
        }

        private static void Write(Coord c, bool draft, string edge, Coord n, float max, float atX, float atZ, float mean, float share, int samples, List<float> crossings, float nearest)
        {
            string roads = crossings.Count == 0 ? "" : string.Join(" ", crossings.ConvertAll(v => v.ToString("0")));
            string line = $"{DateTime.Now:HH:mm:ss},{c.x},{c.z},{(draft ? "draft" : "main")},{edge},{n.x},{n.z},{max:0.00},{atX:0},{atZ:0},{mean:0.00},{share * 100f:0},{samples},{crossings.Count},{(float.IsNaN(nearest) ? "" : nearest.ToString("0"))},{roads}";
            lock (Gate)
            {
                if (_file == null)
                {
                    _file = Path.Combine(BepInEx.Paths.BepInExRootPath, "BurnersUtilities-seams.csv");
                    if (!File.Exists(_file))
                        File.WriteAllText(_file, "time,tile_x,tile_z,detail,edge,neighbour_x,neighbour_z,max_step_m,at_x,at_z,mean_step_m,percent_over_0.25m,samples,road_crossings,nearest_road_to_max_m,road_crossings_at\n");
                }
                File.AppendAllText(_file, line + "\n");
            }
            if (!draft && max >= 1f)
                Plugin.Log.LogInfo($"seam: tile {c.x},{c.z} edge {edge}: step up to {max:0.0} m at ({atX:0}, {atZ:0}), mean {mean:0.00} m, {share * 100f:0}% over 0.25 m; " +
                                   (crossings.Count == 0 ? "no road crosses it" : $"{crossings.Count} road crossing(s), nearest {nearest:0} m from the worst point"));
        }
    }
}
