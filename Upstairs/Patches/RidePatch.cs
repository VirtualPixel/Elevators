using HarmonyLib;
using Upstairs.Services;

namespace Upstairs.Patches
{
    // After the controller has had its say each physics step, a player standing in the moving
    // car takes the car's speed, so the floor never leaves them and never shoves them.
    [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    internal static class RidePatch
    {
        private static void Postfix(PlayerController __instance) => Elevator.CarryLocal(__instance);
    }
}
