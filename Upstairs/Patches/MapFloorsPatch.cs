using HarmonyLib;
using UnityEngine;
using Upstairs.Services;

namespace Upstairs.Patches
{
    // With the map open, the scroll wheel steps through the floors. The game draws whatever
    // layer PlayerLayer says, so the viewed floor is written there each frame before the map
    // lays itself out; its own tracker only writes that field every tenth of a second.
    [HarmonyPatch(typeof(Map), "Update")]
    internal static class MapFloorsPatch
    {
        private static int viewing = -1;

        internal static string ViewingName { get; private set; } = "";

        private static void Prefix(Map __instance)
        {
            if (!__instance.Active)
            {
                viewing = -1;
                ViewingName = "";
                return;
            }
            LiftService? lift = LiftService.Instance;
            if (lift == null || lift.FloorCount < 2)
                return;
            if (viewing < 0)
                viewing = lift.FloorIndexOfLayer(__instance.PlayerLayer);

            float scroll = Input.mouseScrollDelta.y;
            if (scroll > 0.01f || scroll < -0.01f)
            {
                int next = Mathf.Clamp(viewing + (scroll > 0f ? 1 : -1), 0, lift.FloorCount - 1);
                if (next != viewing)
                {
                    viewing = next;
                    if (MenuManager.instance != null && MenuManager.instance.soundTick != null && Camera.main != null)
                        MenuManager.instance.soundTick.Play(Camera.main.transform.position);
                }
            }
            __instance.PlayerLayer = lift.FloorLayer(viewing);
            ViewingName = lift.FloorName(viewing);
        }
    }
}
