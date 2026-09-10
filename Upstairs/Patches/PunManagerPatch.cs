using HarmonyLib;
using Upstairs.Services;

namespace Upstairs.Patches
{
    [HarmonyPatch(typeof(PunManager), "Awake")]
    internal static class PunManagerPatch
    {
        private static void Postfix(PunManager __instance)
        {
            if (__instance.GetComponent<UpstairsNet>() == null)
                __instance.gameObject.AddComponent<UpstairsNet>();
        }
    }
}
