using HarmonyLib;
using Elevators.Services;

namespace Elevators.Patches
{
    // The elevator buttons are the game's own StaticGrabObject, so taking hold of one is the press.
    [HarmonyPatch(typeof(StaticGrabObject), "GrabStarted")]
    internal static class GrabPatch
    {
        private static void Postfix(StaticGrabObject __instance, PhysGrabber player) => Elevator.Grabbed(__instance, player);
    }

    // Letting go ends the hold, so the next take is a press again.
    [HarmonyPatch(typeof(StaticGrabObject), "GrabEnded")]
    internal static class GrabEndPatch
    {
        private static void Postfix(StaticGrabObject __instance) => Elevator.Released(__instance);
    }
}
