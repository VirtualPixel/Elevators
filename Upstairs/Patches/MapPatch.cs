using HarmonyLib;
using UnityEngine;
using Upstairs.Services;

namespace Upstairs.Patches
{
    // The game draws every room outline on one map layer no matter its height, so two floors
    // would overlap on the host's map. Outlines on the new floors go on the layer that matches
    // their height instead, the same layer the game already uses for their walls and doors.
    [HarmonyPatch(typeof(Map), "AddRoomVolume")]
    internal static class MapRoomVolumePatch
    {
        private static Transform? saved;

        private static void Prefix(Map __instance, Vector3 _position)
        {
            saved = null;
            if (!Session.KnowsFloors || !Session.IsOnNewFloor(_position.y))
                return;
            saved = __instance.OverLayerParent;
            __instance.OverLayerParent = __instance.GetLayerParent(_position.y).transform;
        }

        private static void Postfix(Map __instance)
        {
            if (saved == null)
                return;
            __instance.OverLayerParent = saved;
            saved = null;
        }
    }
}
