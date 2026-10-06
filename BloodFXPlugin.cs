using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Y4NGZCompany.Experience.BloodFX;

namespace Y4NGZBloodFX
{
    /// <summary>
    /// Blood and gore visual effects.
    ///
    /// The system owns its own Harmony instance and its own config file. It has no netcode of its
    /// own: every blood event is a client-local presentation of something the game (or another
    /// plugin) has already replicated, so there is nothing here to patch for netcode or to
    /// serialize. Consumers treat this plugin as a soft dependency and degrade without it.
    ///
    /// #393: Awake-only by design — this class has no Update/LateUpdate and no OnDestroy, so it
    /// keeps nothing alive on BepInEx's shared manager object and is immune to that object being
    /// destroyed by another mod's cleanup. If per-frame work or teardown is ever added here, add
    /// a self-owned HideAndDontSave + DontDestroyOnLoad host object first (see
    /// <c>LethalCCTV_Host</c> / <c>Y4NGZCompany_Host</c>).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BloodFXPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.y4ngz.bloodfx";
        public const string PluginName = "BloodVFX";
        public const string PluginVersion = "1.0.0";

        public static BloodFXPlugin Instance { get; private set; }

        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _harmony = new Harmony(PluginGuid);
            BloodFXBootstrap.Initialize(Config, Logger, _harmony);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }
}
