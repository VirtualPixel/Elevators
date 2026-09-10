using HarmonyLib;
using Upstairs.Services;

namespace Upstairs.Patches
{
    // The elevator buttons are the game's own StaticGrabObject, so taking hold of one is the press.
    [HarmonyPatch(typeof(StaticGrabObject), "GrabStarted")]
    internal static class GrabPatch
    {
        private static void Postfix(StaticGrabObject __instance, PhysGrabber player) => Elevator.Grabbed(__instance, player);
    }
}
