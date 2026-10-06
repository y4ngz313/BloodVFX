using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Y4NGZCompany.Experience.BloodFX
{
    /// <summary>
    /// Config surface and Harmony wiring for the blood VFX system. Assets load lazily on the
    /// first hit, so a missing bundle costs nothing at startup.
    /// </summary>
    public static class BloodFXBootstrap
    {
        private const string ConfigSection = "08 - Blood FX";

        internal static ManualLogSource Log { get; private set; }

        internal static ConfigEntry<bool> Enabled { get; private set; }
        internal static ConfigEntry<int> WorldDecalCap { get; private set; }
        internal static ConfigEntry<int> BodyDecalCap { get; private set; }
        internal static ConfigEntry<int> CreatureBodyDecalCap { get; private set; }
        internal static ConfigEntry<int> FinisherBodyDecalCap { get; private set; }
        internal static ConfigEntry<bool> EnableBundyDamageBlood { get; private set; }
        internal static ConfigEntry<float> IntensityMultiplier { get; private set; }
        internal static ConfigEntry<float> DecalFadeSeconds { get; private set; }
        internal static ConfigEntry<float> MaxEventsPerSecond { get; private set; }
        internal static ConfigEntry<float> MaxViewerDistance { get; private set; }
        internal static ConfigEntry<bool> SuppressVanillaBlood { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static ConfigEntry<bool> EnableDecalsOnRoomMaterials { get; private set; }
        internal static ConfigEntry<bool> IsolateBodyDecalsOnDecalLayer { get; private set; }

        // ------------------------------------------------------------- public scalars
        //
        // Cross-plugin consumers (Bundy in Y4NGZCompany) read settings, never ConfigEntry
        // objects: a scalar keeps BepInEx.Configuration out of the boundary and lets the
        // defaults below stand in unchanged when this plugin has not bound its config yet.

        /// <summary>Master switch. False before <see cref="Initialize"/> has bound the config.</summary>
        public static bool BloodFXEnabled => Enabled?.Value ?? false;

        /// <summary>Whether damage dealt to Bundy may place replicated body decals.</summary>
        public static bool BundyDamageBloodEnabled => EnableBundyDamageBlood?.Value ?? true;

        /// <summary>Most blood decals carried on one creature body.</summary>
        public static int CreatureBodyDecalCapValue => CreatureBodyDecalCap?.Value ?? 10;

        /// <summary>Config-gated diagnostics channel. One line per blood event when enabled.</summary>
        public static bool Verbose => DebugLogging?.Value ?? false;

        public static void Trace(string message)
        {
            if (!Verbose)
                return;

            Log?.LogInfo("[Y4NGZCompany.BloodFX] " + message);
        }

        internal static void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            Log = logger;
            TryImportLegacyConfig(config);
            BindConfig(config);

            if (!Enabled.Value)
            {
                Log?.LogInfo("[Y4NGZCompany.BloodFX] Disabled via config.");
                return;
            }

            harmony.PatchAll(typeof(BloodFXPatches));
            harmony.PatchAll(typeof(BloodFXRoundLifecyclePatches));
            harmony.PatchAll(typeof(BloodFXDecalShieldPatches));

            Log?.LogInfo(
                "[Y4NGZCompany.BloodFX] Ready: " +
                $"worldDecalCap={WorldDecalCap.Value}, bodyDecalCap={BodyDecalCap.Value}, " +
                $"creatureBodyDecalCap={CreatureBodyDecalCap.Value}, " +
                $"finisherBodyDecalCap={FinisherBodyDecalCap.Value}, " +
                $"bundyDamageBlood={EnableBundyDamageBlood.Value}, " +
                $"intensity={IntensityMultiplier.Value}, decalFadeSeconds={DecalFadeSeconds.Value}, "
                + $"maxEventsPerSecond={MaxEventsPerSecond.Value}, " +
                $"maxViewerDistance={MaxViewerDistance.Value}, " +
                $"suppressVanillaBlood={SuppressVanillaBlood.Value}, " +
                $"debugLogging={DebugLogging.Value}.");
        }

        /// <summary>
        /// One-way, best-effort migration of the settings this system had while it lived inside
        /// Y4NGZCompany. The section name and every key are unchanged, so a player's dialled-in
        /// values survive the split without being retyped.
        ///
        /// Runs only when this plugin's own file does not yet carry the section, so a value edited
        /// here is never overwritten by the stale copy left behind in the old file. The old file is
        /// read as text and never written: Y4NGZCompany still owns it and still binds its own keys.
        /// Every failure is swallowed - a config that cannot be imported must not stop the plugin.
        /// </summary>
        private static void TryImportLegacyConfig(ConfigFile config)
        {
            const string legacyConfigFileName = "com.y4ngz.company.cfg";
            string sectionHeader = "[" + ConfigSection + "]";

            try
            {
                string targetPath = config?.ConfigFilePath;
                if (string.IsNullOrEmpty(targetPath))
                    return;

                if (File.Exists(targetPath) &&
                    File.ReadAllText(targetPath).IndexOf(sectionHeader, StringComparison.Ordinal) >= 0)
                {
                    return;
                }

                string legacyPath = Path.Combine(Paths.ConfigPath, legacyConfigFileName);
                if (!File.Exists(legacyPath))
                    return;

                var imported = new List<string>();
                bool inSection = false;
                foreach (string rawLine in File.ReadAllLines(legacyPath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal))
                    {
                        inSection = string.Equals(line, sectionHeader, StringComparison.Ordinal);
                        continue;
                    }

                    if (!inSection || line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int split = line.IndexOf('=');
                    if (split <= 0)
                        continue;

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    if (key.Length == 0)
                        continue;

                    imported.Add(key + " = " + value);
                }

                if (imported.Count == 0)
                    return;

                var text = new StringBuilder();
                if (File.Exists(targetPath))
                {
                    text.Append(File.ReadAllText(targetPath));
                    text.AppendLine();
                }
                else
                {
                    string directory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                }

                text.AppendLine(sectionHeader);
                text.AppendLine();
                for (int i = 0; i < imported.Count; i++)
                    text.AppendLine(imported[i]);

                File.WriteAllText(targetPath, text.ToString());
                config.Reload();

                Log?.LogInfo(
                    $"[Y4NGZCompany.BloodFX] Imported {imported.Count} setting(s) from "
                    + $"'{legacyConfigFileName}' into '{Path.GetFileName(targetPath)}': "
                    + string.Join(", ", imported.ToArray()) + ".");
            }
            catch (Exception e)
            {
                Log?.LogWarning(
                    "[Y4NGZCompany.BloodFX] Legacy config import skipped ("
                    + e.GetType().Name + ": " + e.Message + "); defaults are in force.");
            }
        }

        private static void BindConfig(ConfigFile config)
        {
            Enabled = config.Bind(ConfigSection, "Enabled", true,
                "Master switch for the blood burst, splatter, and body-blood system.");

            WorldDecalCap = config.Bind(ConfigSection, "World Decal Cap", 48,
                new ConfigDescription(
                    "Most wall and floor blood decals alive at once. Decals last the whole round; "
                    + "past this many the oldest is recycled. Set to 0 to place none.",
                    new AcceptableValueRange<int>(0, 256)));

            BodyDecalCap = config.Bind(ConfigSection, "Body Decal Cap Per Player", 4,
                new ConfigDescription(
                    "Most blood decals carried on one player's body. They survive healing and clear "
                    + "on respawn or round end. Set to 0 to disable body blood.",
                    new AcceptableValueRange<int>(0, 12)));

            CreatureBodyDecalCap = config.Bind(ConfigSection, "Creature Body Decal Cap", 10,
                new ConfigDescription(
                    "Most blood decals carried on one creature body. Creature blood has its own "
                    + "channel so it cannot evict player hit or finisher blood. Set to 0 to disable it.",
                    new AcceptableValueRange<int>(0, 24)));

            FinisherBodyDecalCap = config.Bind(ConfigSection, "Finisher Body Decal Cap", 6,
                new ConfigDescription(
                    "Most blood decals carried in one player's finisher channel. This is separate from "
                    + "ordinary hit blood and clears with the other body channels on respawn or round end. "
                    + "Set to 0 to disable finisher body blood.",
                    new AcceptableValueRange<int>(0, 16)));

            EnableBundyDamageBlood = config.Bind(ConfigSection, "Enable Bundy Damage Blood", true,
                "Allow damage dealt to Bundy to place replicated blood decals on his body. "
                + "Set off to keep the general Blood FX system while disabling Bundy damage blood.");

            IntensityMultiplier = config.Bind(ConfigSection, "Intensity Multiplier", 1f,
                new ConfigDescription(
                    "Scales particle emission and the number of decals placed per hit. "
                    + "Damage amount still picks the small/medium/heavy tier.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            DecalFadeSeconds = config.Bind(ConfigSection, "Decal Fade Seconds", 1.2f,
                new ConfigDescription(
                    "How long a recycled blood decal takes to dissolve away. When a cap is reached the "
                    + "oldest decal fades out over this many seconds instead of vanishing mid-view. A "
                    + "fading decal has already left the cap queue, so this never costs you live blood. "
                    + "Set to 0 to remove recycled decals instantly, as before.",
                    new AcceptableValueRange<float>(0f, 10f)));

            MaxEventsPerSecond = config.Bind(ConfigSection, "Max Blood Events Per Second", 2f,
                new ConfigDescription(
                    "Most blood events one player can produce per second. Damage-over-time sources "
                    + "(turret bursts, mines, drowning, the chainsaw) otherwise fire up to eight times a "
                    + "second each and churn the whole world decal cap in a couple of seconds. Hits past "
                    + "the limit are skipped entirely — no burst, no splatter, no body blood. "
                    + "Set to 0 for no limit (duplicate-event suppression still applies).",
                    new AcceptableValueRange<float>(0f, 20f)));

            MaxViewerDistance = config.Bind(ConfigSection, "Max Viewer Distance", 100f,
                new ConfigDescription(
                    "Metres from your camera past which a hit places no burst, spray, or wall/floor "
                    + "splatter. Damage runs on every client, so without this a hit across the map spends "
                    + "world decal slots on blood nobody will ever see — evicting the blood in the room "
                    + "you are standing in. Body blood and death puddles ignore this, because both persist "
                    + "and can be walked up to later. Set to 0 to place blood at any distance.",
                    new AcceptableValueRange<float>(0f, 500f)));

            SuppressVanillaBlood = config.Bind(ConfigSection, "Suppress Vanilla Blood Pools", true,
                "Stop the game's own pooled blood splats from spawning so only this system's blood shows. "
                + "Snow footprints are unaffected.");

            DebugLogging = config.Bind(ConfigSection, "Debug Logging", false,
                "Write one diagnostic line per blood event (hit, environment splatter, body decal, death) "
                + "plus a one-time material audit of each player's suit. Hits are rare, so the volume is low; "
                + "turn it off once the system is dialled in.");

            EnableDecalsOnRoomMaterials = config.Bind(ConfigSection, "Enable Decals On Room Materials", true,
                "Interior wall and tile materials ship with HDRP's 'Receive Decals' turned off, so blood "
                + "sprayed onto a wall was placed but never drawn (floors receive, walls did not). When on, "
                + "the first blood decal to land on a surface flips that material to decal-receiving. "
                + "Visual-only, applies to static room geometry, and each material is processed once.");

            IsolateBodyDecalsOnDecalLayer = config.Bind(ConfigSection, "Isolate Body Decals On Decal Layer", false,
                "ESCAPE HATCH, off by default. Puts body blood on HDRP DecalLayer7 and grants that layer only "
                + "to the wounded player's renderers, so the projector physically cannot paint anything else. "
                + "Verified broken in the shipped build: the HDRP asset reports supportDecalLayers=true but no "
                + "shader in the game data carries the DECAL_LAYERS variant, so layered decals draw nothing at "
                + "all. Held items are kept clean by the item material shield instead. Only turn this on if a "
                + "future game or mod update ships the missing shader variants.");
        }
    }
}
