using HarmonyLib;
using Elevators.Configuration;
using Elevators.Services;

namespace Elevators.Patches
{
    // SetupHost gathers every valuable spot in the scene on its first step, so the new floors'
    // spots are hidden just before it starts and put back once the ground floor is done.
    [HarmonyPatch(typeof(ValuableDirector), "SetupHost")]
    internal static class SetupHostPatch
    {
        private static void Prefix()
        {
            if (Session.Active && SemiFunc.IsMasterClientOrSingleplayer())
                LootPass.HideNewFloors();
        }
    }

    [HarmonyPatch(typeof(ValuableDirector), "VolumesAndSwitchSetup")]
    internal static class VolumesAndSwitchSetupPatch
    {
        private static void Prefix(ValuableDirector __instance)
        {
            if (Session.Active && SemiFunc.IsMasterClientOrSingleplayer() && PluginConfig.ExtraLoot.Value > 0)
                LootPass.FillNewFloors(__instance);
        }
    }
}
