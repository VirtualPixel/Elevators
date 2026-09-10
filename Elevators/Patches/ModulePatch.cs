using HarmonyLib;
using Elevators.Services;

namespace Elevators.Patches
{
    // The game tells each room which sides have a neighbour and the room opens those walls. The
    // lift room gets one more side opened, with no door object in it, and the elevator stands there.
    [HarmonyPatch(typeof(Module), "ModuleConnectionSet")]
    internal static class ModuleConnectionPatch
    {
        private static void Prefix(Module __instance, ref bool _top, ref bool _bottom, ref bool _right, ref bool _left)
        {
            if (Session.LiftSide < 0 || !Session.IsGroundLiftModule(__instance))
                return;
            switch (Session.LiftSide)
            {
                case 0: _top = true; break;
                case 1: _right = true; break;
                case 2: _bottom = true; break;
                case 3: _left = true; break;
            }
            Plugin.LogInfo($"[Plan] opened side {Session.LiftSide} of the lift room for the elevator");
        }
    }
}
