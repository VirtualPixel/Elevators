using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Elevators.Services
{
    // REPOLib 4.2.0 applies its patches inside one try block, and its SteamManager patch throws on this
    // game build, so the six after it never run. One of those lets PrefabRef find the prefabs REPOLib
    // registered. Apply the skipped ones here until REPOLib is fixed. Does nothing if REPOLib, or the
    // other mod, already did.
    internal static class REPOLibCompat
    {
        private const string CompatId = "Vippy.REPOLibCompat";

        private static readonly string[] Skipped =
        {
            "PlayerControllerPatch", "DebugCommandHandlerPatch", "ResourcesPatch",
            "SplashScreenPatch", "MetaManagerPatch", "PlayerCosmeticsPatch",
        };

        internal static void Apply(ManualLogSource log)
        {
            var load = AccessTools.Method(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Type) });
            var owners = Harmony.GetPatchInfo(load)?.Prefixes.Select(p => p.owner).ToList() ?? new System.Collections.Generic.List<string>();
            if (owners.Contains("REPOLib") || owners.Contains(CompatId))
                return;

            var harmony = new Harmony(CompatId);
            int applied = 0;
            foreach (string name in Skipped)
            {
                Type? type = AccessTools.TypeByName("REPOLib.Patches." + name);
                if (type == null)
                    continue;
                try
                {
                    harmony.PatchAll(type);
                    applied++;
                }
                catch (Exception e)
                {
                    log.LogWarning($"REPOLib's {name} could not be applied: {e.Message}");
                }
            }
            log.LogInfo($"REPOLib skipped its patches after SteamManager on this game build, applied {applied} of {Skipped.Length} here");
        }
    }
}
