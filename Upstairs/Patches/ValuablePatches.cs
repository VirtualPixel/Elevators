using System.Collections;
using HarmonyLib;
using Upstairs.Configuration;
using Upstairs.Services;

namespace Upstairs.Patches
{
    [HarmonyPatch(typeof(ValuableDirector), "SpawnValuable")]
    internal static class SpawnValuablePatch
    {
        private static void Postfix(ValuableVolume _volume) => LootPass.Used.Add(_volume);
    }

    [HarmonyPatch(typeof(ValuableDirector), "SetupHost")]
    internal static class SetupHostPatch
    {
        private static void Postfix(ValuableDirector __instance, ref IEnumerator __result)
        {
            if (Session.Active && PluginConfig.ExtraLoot.Value > 0)
                __result = LootPass.After(__instance, __result);
        }
    }
}
