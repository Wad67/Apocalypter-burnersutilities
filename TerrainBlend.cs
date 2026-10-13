using System;
using System.Collections.Generic;
using Den.Tools.Matrices;
using HarmonyLib;
using MapMagic.Nodes.MatrixGenerators;
using MapMagic.Products;
using MapMagic.Terrains;

namespace BurnersUtilities
{
    // Faster terrain texturing. MapMagic's TexturesOutput200.BlendLayers turns each terrain layer's weight map into the
    // terrain's splat map: per pixel, weight times mask for every layer, normalised to add up to one. It reads every
    // layer and mask through the IList interface four times per layer per pixel, twice over, on an array passed as an
    // IList of its base type; under Mono every one of those reads goes through a type-checking wrapper. With the
    // terrain's resolution and layer count that is hundreds of millions of calls per tile, most of MapMagic's time.
    //
    // This does the same sums in the same order (so the result is the same to the bit) on plain arrays read once,
    // and copies the result into the terrain's 3D array in one block. Anything unexpected falls back to MapMagic's own.
    internal static class TerrainBlend
    {
        private static bool Enabled => Plugin.FastTerrainEntry != null && Plugin.FastTerrainEntry.Value;
        private static int _blended;

        [HarmonyPatch(typeof(TexturesOutput200), nameof(TexturesOutput200.BlendLayers))]
        private static class BlendPatch
        {
            private static bool Prefix(IList<Matrix> matrices, IList<Matrix> masks, Area area, IList<int> channelNumbers, StopToken stop, ref float[,,] __result)
            {
                if (!Enabled || matrices == null || masks == null || area == null) return true;
                try
                {
                    __result = Blend(matrices, masks, area, channelNumbers, stop, out bool handled);
                    return !handled;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("fast terrain blend: " + e.Message + "; MapMagic's own blend instead");
                    return true;
                }
            }
        }

        internal static float[,,] Blend(IList<Matrix> matrices, IList<Matrix> masks, Area area, IList<int> channelNumbers, StopToken stop, out bool handled)
        {
            handled = false;
            int size = area.active.rect.size.x;
            int margins = area.Margins;
            var full = area.full.rect;
            int channels;
            if (channelNumbers != null)
            {
                int max = 0;
                foreach (int c in channelNumbers) if (c > max) max = c;
                channels = max + 1;
            }
            else channels = matrices.Count;
            // MapMagic reads matrices[k] and masks[k] for every channel index: if the lists are shorter, let it
            // fail its own way
            if (matrices.Count < channels || masks.Count < channels || (channelNumbers != null && channelNumbers.Count < channels)) return null;

            var layer = new float[channels][];
            var mask = new float[channels][];
            var target = new int[channels];
            for (int k = 0; k < channels; k++)
            {
                layer[k] = matrices[k]?.arr;
                mask[k] = masks[k]?.arr;
                target[k] = channelNumbers != null ? channelNumbers[k] : k;
            }

            var flat = new float[size * size * channels];
            for (int i = 0; i < size; i++)
            {
                if (stop != null && stop.stop) { handled = true; return null; }
                for (int j = 0; j < size; j++)
                {
                    // full.GetPos(i + full.offset.x + margins, j + full.offset.z + margins)
                    int pos = (j + full.offset.z + margins - full.offset.z) * full.size.x + (i + full.offset.x + margins) - full.offset.x;
                    float sum = 0f;
                    for (int k = 0; k < channels; k++)
                    {
                        float v = layer[k] != null ? layer[k][pos] : 0f;
                        v *= mask[k] == null ? 1f : mask[k][pos];
                        sum += v;
                    }
                    if (sum == 0f) continue;
                    int cell = (j * size + i) * channels;
                    for (int l = 0; l < channels; l++)
                    {
                        float v = layer[l] != null ? layer[l][pos] : 0f;
                        v *= mask[l] == null ? 1f : mask[l][pos];
                        v /= sum;
                        if (v < 0f) v = 0f;
                        if (v > 1f) v = 1f;
                        flat[cell + target[l]] += v;
                    }
                }
            }
            var result = new float[size, size, channels];
            Buffer.BlockCopy(flat, 0, result, 0, flat.Length * sizeof(float));
            handled = true;
            int n = System.Threading.Interlocked.Increment(ref _blended);
            if (n == 1 || n % 10 == 0) Plugin.Log.LogInfo($"fast terrain texturing: {n} tile(s) blended ({size} x {size}, {channels} layers)");
            return result;
        }
    }
}
