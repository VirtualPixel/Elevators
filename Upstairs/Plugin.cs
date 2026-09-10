using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Upstairs.Configuration;
using Upstairs.Services;
using Upstairs.UI;

namespace Upstairs
{
    [BepInPlugin("Vippy.Upstairs", "Upstairs", BuildInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal new static ManualLogSource Logger => Instance.BaseLogger;
        private ManualLogSource BaseLogger => base.Logger;

        private void Awake()
        {
            Instance = this;
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            PluginConfig.Init(Config);
            gameObject.AddComponent<LiftService>();
            gameObject.AddComponent<Announcer>();
            gameObject.AddComponent<LiftHud>();
            new Harmony(Info.Metadata.GUID).PatchAll();

            Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} loaded. LogLevel={PluginConfig.LoggingLevel.Value}");
        }

        internal static void LogAlways(string msg) => Logger.LogInfo(msg);

        internal static void LogInfo(string msg)
        {
            if (PluginConfig.LoggingLevel.Value >= VerbosityLevel.Debug)
                Logger.LogInfo(msg);
        }

        internal static void LogVerbose(string msg)
        {
            if (PluginConfig.LoggingLevel.Value >= VerbosityLevel.Verbose)
                Logger.LogDebug(msg);
        }
    }
}
