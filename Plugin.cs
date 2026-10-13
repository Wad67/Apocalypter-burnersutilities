using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BurnersUtilities
{
    // Small things in one plugin, each switched on and off on its own:
    //   Ride Along  standing on something that moves carries you with it (PlatformRide)
    //   Ropes       rope kits for tying ropes or cables between any two things (RopeTool, Rope, RopeKinds)
    //   Speedups    faster save loading (SaveCache), terrain texturing (TerrainBlend) and PlayMaker events (FsmBroadcast)
    //   Diagnostics terrain seam log (SeamLog), FSM dump (FsmDump)
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.burner.apocalypter.sigmamod", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.burner.apocalypter.burnersutilities";
        public const string NAME = "Burner's Utilities";
        public const string VERSION = "0.2.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> RideAlongEntry, RopesEntry;
        public static ConfigEntry<float> ReachEntry;
        public static ConfigEntry<KeyCode> PutAwayKeyEntry;
        public static ConfigEntry<bool> VerboseLogEntry;
        public static ConfigEntry<bool> FastLoadEntry;
        public static ConfigEntry<bool> FastTerrainEntry;
        public static ConfigEntry<bool> FastFsmEventsEntry;
        public static ConfigEntry<bool> TerrainSeamsEntry;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;

            // opt in to the Apocasetter Mods window (the entry itself is hidden there)
            Config.Bind("General", "Apocasetter", true, "Show in the Apocasetter Mods menu.");
            RideAlongEntry = Config.Bind("Ride Along", "Enabled", true, "Standing on a moving vehicle carries you with it.");
            RopesEntry = Config.Bind("Ropes", "Enabled", true, "Rope kits: tie ropes and cables between any two things.");
            ReachEntry = Config.Bind("Ropes", "Reach", 3f, new ConfigDescription("How far you can tie a rope, m.", new AcceptableValueRange<float>(1f, 10f)));
            PutAwayKeyEntry = Config.Bind("Ropes", "PutAwayKey", KeyCode.X, "Key that puts the ropes away.");
            FastLoadEntry = Config.Bind("Saves", "FastLoad", true, "Read each save once and look keys up directly. Takes effect on the next load.");
            FastTerrainEntry = Config.Bind("Terrain", "FastTexturing", true, "Faster terrain texture blending (same result). Takes effect on the next tile generated.");
            FastFsmEventsEntry = Config.Bind("PlayMaker", "FastEvents", true, "Find the receivers of PlayMaker events sent to an object directly instead of searching every FSM (same receivers, same order).");
            VerboseLogEntry = Config.Bind("Debug", "VerboseLog", false, "Extra diagnostics and an FSM dump in BepInEx.");
            TerrainSeamsEntry = Config.Bind("Debug", "TerrainSeams", false, "Log how far terrain tiles disagree at their edges (BepInEx/BurnersUtilities-seams.csv).");
            DropOldSettings();
            RopeKinds.Init(Paths.ConfigPath);

            new Harmony(GUID).PatchAll(typeof(Plugin).Assembly);
            SeamLog.Init();
            FsmDump.Init();

            _runner = new GameObject("BurnersUtilities Runner");
            _runner.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(_runner);
            _runner.AddComponent<RopeTool>();
            _runner.AddComponent<Runner>();

            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }

        // settings from earlier versions (rope strength and thickness, now per kit in the rope kit table) stay in
        // the .cfg as orphans unless removed
        private void DropOldSettings()
        {
            var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Config) as Dictionary<ConfigDefinition, string>;
            if (orphans == null || orphans.Count == 0) return;
            orphans.Clear();
            Config.Save();
        }

        public static void Verbose(string msg)
        {
            if (VerboseLogEntry.Value) Log.LogInfo(msg);
        }
    }

    // main-thread tick, independent of the plugin object's lifetime
    internal class Runner : MonoBehaviour
    {
        private void Update() { FsmDump.Tick(); }
    }
}
