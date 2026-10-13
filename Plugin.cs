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
    //   Ropes       rope kits for tying ropes, cables or hoses between any two things (RopeTool, Rope, RopeKinds, Hose)
    //   Hands       holding grabbed things by force instead of placing them (HandHold)
    //   Nails       a nail gun nailing things together or to the world (NailGun, NailBoard)
    //   Speedups    faster save loading (SaveCache), terrain texturing (TerrainBlend) and PlayMaker events (FsmBroadcast)
    //   Diagnostics terrain seam log (SeamLog), FSM dump (FsmDump)
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.burner.apocalypter.sigmamod", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.burner.apocalypter.burnersutilities";
        public const string NAME = "Burner's Utilities";
        public const string VERSION = "0.3.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> RideAlongEntry, RopesEntry;
        public static ConfigEntry<float> ReachEntry;
        public static ConfigEntry<KeyCode> PutAwayKeyEntry;
        public static ConfigEntry<bool> VerboseLogEntry;
        public static ConfigEntry<bool> FastLoadEntry;
        public static ConfigEntry<bool> FastTerrainEntry;
        public static ConfigEntry<bool> FastFsmEventsEntry;
        public static ConfigEntry<bool> TerrainSeamsEntry;
        public static ConfigEntry<bool> PhysLockEntry;
        public static ConfigEntry<bool> NailsEntry;
        public static ConfigEntry<bool> HandHoldEntry;
        public static ConfigEntry<float> HandStrengthEntry, HandStiffnessEntry, HandHeaveEntry, HandStrainDamageEntry;

        private static GameObject _runner;

        private void Awake()
        {
            Log = Logger;

            // opt in to the Apocasetter Mods window (the entry itself is hidden there)
            Config.Bind("General", "Apocasetter", true, "Show in the Apocasetter Mods menu.");
            RideAlongEntry = Config.Bind("Ride Along", "Enabled", true, "Standing on a moving vehicle carries you with it.");
            RopesEntry = Config.Bind("Ropes", "Enabled", true, "Rope and hose kits: tie ropes, cables and hoses between any two things.");
            ReachEntry = Config.Bind("Ropes", "Reach", 3f, new ConfigDescription("How far you can tie a rope, m.", new AcceptableValueRange<float>(1f, 10f)));
            HandHoldEntry = Config.Bind("Hands", "HoldByForce", true, "Hold grabbed things by force, so what they are tied or nailed to pulls back.");
            HandStrengthEntry = Config.Bind("Hands", "Strength", 1200f, new ConfigDescription("How hard the hands push or pull a held thing, N.", new AcceptableValueRange<float>(200f, 20000f)));
            HandStiffnessEntry = Config.Bind("Hands", "Stiffness", 1500f, new ConfigDescription("How firmly the hands hold a thing where it is held, N/m.", new AcceptableValueRange<float>(100f, 20000f)));
            HandHeaveEntry = Config.Bind("Hands", "HeaveStrength", 5500f, new ConfigDescription("The most the hands give while the button is held after taking hold, N (it hurts).", new AcceptableValueRange<float>(200f, 20000f)));
            HandStrainDamageEntry = Config.Bind("Hands", "StrainDamage", 2f, new ConfigDescription("Health lost a second for each kN heaved past Strength.", new AcceptableValueRange<float>(0f, 50f)));
            NailsEntry = Config.Bind("Nails", "Enabled", true, "The nail gun: nail things together, or to the world.");
            PutAwayKeyEntry = Config.Bind("Ropes", "PutAwayKey", KeyCode.X, "Key that puts the ropes away.");
            FastLoadEntry = Config.Bind("Saves", "FastLoad", true, "Read each save once and look keys up directly. Takes effect on the next load.");
            FastTerrainEntry = Config.Bind("Terrain", "FastTexturing", true, "Faster terrain texture blending (same result). Takes effect on the next tile generated.");
            PhysLockEntry = Config.Bind("Physlock", "InnermostVolume", true, "Items lock into the innermost lock volume they are in (a crate's, not the trailer around it).");
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
            _runner.AddComponent<HandHold>();
            _runner.AddComponent<RopeTool>();
            _runner.AddComponent<NailBoard>();
            _runner.AddComponent<NailGun>();
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
