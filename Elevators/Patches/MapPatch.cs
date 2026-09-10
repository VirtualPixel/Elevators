using HarmonyLib;
using UnityEngine;
using Elevators.Services;

namespace Elevators.Patches
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

        private static void Postfix(Map __instance, MapModule __result)
        {
            // The game bakes a room's real height into its map object, a tenth of a metre per
            // metre, which puts a room forty metres up four map units above its layer, in view
            // from every floor. The layer already carries the height, so the object sits at
            // zero and its outlines with it.
            if (__result != null)
            {
                Transform module = __result.transform;
                module.localPosition = new Vector3(module.localPosition.x, 0f, module.localPosition.z);
                // The fill and the outline hang off the module object too, and both carry the game's
                // MapObject on their root; only the module's own graphics keep their place.
                foreach (Transform child in module)
                {
                    if (child == __result.graphic || child.name.StartsWith("Graphic"))
                        continue;
                    child.localPosition = new Vector3(child.localPosition.x, 0f, child.localPosition.z);
                }
            }
            if (saved == null)
                return;
            __instance.OverLayerParent = saved;
            saved = null;
        }
    }
}
