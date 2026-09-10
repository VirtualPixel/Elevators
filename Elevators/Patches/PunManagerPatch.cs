using HarmonyLib;
using Elevators.Services;

namespace Elevators.Patches
{
    [HarmonyPatch(typeof(PunManager), "Awake")]
    internal static class PunManagerPatch
    {
        private static void Postfix(PunManager __instance)
        {
            if (__instance.GetComponent<ElevatorsNet>() == null)
                __instance.gameObject.AddComponent<ElevatorsNet>();
        }
    }
}
