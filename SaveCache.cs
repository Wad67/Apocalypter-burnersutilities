using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using ES3Internal;
using HarmonyLib;

namespace BurnersUtilities
{
    // Faster save loading. Easy Save finds a key in a save file by reading it from the top, parsing and skipping
    // every key before the one it wants; each load (and each "does this key exist") opens the file and does it again.
    // The game loads hundreds of keys from a save of a few MB, so a load reads the save hundreds of times over.
    //
    // This indexes each save file once: where each top-level key's entry starts and ends. When Easy Save then goes to a
    // key, its reader is handed just that entry, exactly as written in the file, and parses it with its own code as
    // before. A key that isn't there is answered at once. The index is rebuilt when the file's size or write time
    // changes, so saving in between is picked up. Only plain JSON files on disk are indexed (the game's saves);
    // encrypted, compressed or in-memory reads go through Easy Save untouched. Writing is never touched.
    internal static class SaveCache
    {
        private sealed class Index
        {
            public string Path;
            public long Length;
            public DateTime Written;
            public string Text;
            public Dictionary<string, int[]> Spans;   // key -> { start (at the key's opening quote), end }
        }

        private static readonly Dictionary<string, Index> Files = new Dictionary<string, Index>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConditionalWeakTable<ES3Reader, Index> Readers = new ConditionalWeakTable<ES3Reader, Index>();
        private static readonly object Gate = new object();
        private static int _hits, _misses;
        private static readonly byte[] Empty = Encoding.UTF8.GetBytes("}");

        private static bool Enabled => Plugin.FastLoadEntry != null && Plugin.FastLoadEntry.Value;

        // the index for a save file, built or rebuilt if the file changed; null if it can't or shouldn't be indexed
        private static Index For(ES3Settings settings)
        {
            if (settings == null || settings.location != ES3.Location.File || settings.format != ES3.Format.JSON
                || settings.encryptionType != ES3.EncryptionType.None || settings.compressionType != ES3.CompressionType.None)
                return null;
            string path = settings.FullPath;
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            lock (Gate)
            {
                if (Files.TryGetValue(path, out var have) && have.Length == info.Length && have.Written == info.LastWriteTimeUtc)
                    return have;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string text;
                using (var reader = new StreamReader(path)) text = reader.ReadToEnd();
                var spans = Build(text);
                if (spans == null)
                {
                    Plugin.Log.LogWarning($"save cache: {Path.GetFileName(path)} isn't the JSON it expected; loading it the slow way");
                    Files.Remove(path);
                    return null;
                }
                var index = new Index { Path = path, Length = info.Length, Written = info.LastWriteTimeUtc, Text = text, Spans = spans };
                Files[path] = index;
                // the settings file is rewritten and re-read often; only report real saves unless asked
                if (text.Length >= 64 * 1024 || Plugin.VerboseLogEntry.Value)
                    Plugin.Log.LogInfo($"save cache: indexed {Path.GetFileName(path)}: {spans.Count} keys, {text.Length / 1024} kB, {watch.ElapsedMilliseconds} ms");
                return index;
            }
        }

        // top-level keys of an Easy Save JSON file and where each entry ("key" : { ... }) starts and ends
        internal static Dictionary<string, int[]> Build(string s)
        {
            var spans = new Dictionary<string, int[]>(StringComparer.Ordinal);
            int i = 0, n = s.Length;
            SkipSpace(s, ref i);
            if (i >= n || s[i] != '{') return null;
            i++;
            while (true)
            {
                while (i < n && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                if (i >= n) return null;
                if (s[i] == '}') return spans;
                if (s[i] != '"') return null;
                int start = i;
                string key = ReadString(s, ref i);
                if (key == null) return null;
                SkipSpace(s, ref i);
                if (i >= n || s[i] != ':') return null;
                i++;
                SkipSpace(s, ref i);
                if (!SkipValue(s, ref i)) return null;
                if (!spans.ContainsKey(key)) spans[key] = new[] { start, i };   // Easy Save goes to the first
            }
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        // a quoted string from its opening quote; i ends after the closing one. Unescaped as Easy Save's own reader
        // does (Read_string): \b \f \n \r \t become control characters, any other escaped character is kept as itself
        private static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            for (i++; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\')
                {
                    if (++i >= s.Length) return null;
                    c = s[i];
                    switch (c)
                    {
                        case 'b': c = '\b'; break;
                        case 'f': c = '\f'; break;
                        case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break;
                        case 't': c = '\t'; break;
                    }
                }
                sb.Append(c);
            }
            return null;
        }

        // one value: an object or array by matching brackets outside strings, anything else up to the next separator
        private static bool SkipValue(string s, ref int i)
        {
            int depth = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    for (i++; i < s.Length && s[i] != '"'; i++)
                        if (s[i] == '\\') i++;
                    if (i >= s.Length) return false;
                    if (depth == 0) { i++; return true; }
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth == 0) return true;   // the end of the enclosing object: an empty value
                    if (--depth == 0) { i++; return true; }
                }
                else if (depth == 0 && c == ',') return true;
            }
            return false;
        }

        // the index is read before Easy Save opens the file (it opens it exclusively) and attached to the reader after
        [HarmonyPatch(typeof(ES3Reader), nameof(ES3Reader.Create), typeof(ES3Settings))]
        private static class CreatePatch
        {
            private static void Prefix(ES3Settings settings, out Index __state)
            {
                __state = null;
                if (!Enabled) return;
                try { __state = For(settings); }
                catch (Exception e) { Plugin.Log.LogWarning("save cache: " + e.Message + "; loading the slow way"); }
            }

            private static void Postfix(ES3Reader __result, Index __state)
            {
                if (__state != null && __result is ES3JSONReader) Readers.Add(__result, __state);
            }
        }

        // going to a key: hand the reader just that entry (or nothing, if the key isn't there) and let Easy Save read it
        [HarmonyPatch(typeof(ES3Reader), "Goto")]
        private static class GotoPatch
        {
            private static void Prefix(ES3Reader __instance, string key)
            {
                if (key == null || !(__instance is ES3JSONReader json) || !Readers.TryGetValue(__instance, out var index)) return;
                byte[] bytes;
                if (index.Spans.TryGetValue(key, out var span))
                {
                    bytes = Encoding.UTF8.GetBytes(index.Text.Substring(span[0], span[1] - span[0]) + "}");
                    _hits++;
                }
                else
                {
                    bytes = Empty;
                    _misses++;
                }
                json.baseReader.Dispose();
                json.baseReader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
                if (Plugin.VerboseLogEntry.Value && (_hits + _misses) % 100 == 0)
                    Plugin.Verbose($"save cache: {_hits} keys read from the index, {_misses} answered missing");
            }
        }
    }
}
