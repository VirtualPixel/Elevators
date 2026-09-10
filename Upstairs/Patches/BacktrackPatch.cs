using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Upstairs.Services;

namespace Upstairs.Patches
{
    // The map's route to the truck or the extraction point is a navmesh path from where the
    // player last stood. From another floor there is no such path, so when the destination is on
    // a different floor the route runs to the nearest elevator landing on this one instead.
    [HarmonyPatch(typeof(NavMesh), nameof(NavMesh.CalculatePath), new[] { typeof(Vector3), typeof(Vector3), typeof(int), typeof(NavMeshPath) })]
    internal static class BacktrackPatch
    {
        private static void Prefix(Vector3 sourcePosition, ref Vector3 targetPosition)
        {
            if (!Session.KnowsFloors || Map.Instance == null || !Map.Instance.Active)
                return;
            PlayerAvatar me = PlayerAvatar.instance;
            if (me == null || sourcePosition != me.LastNavmeshPosition)
                return;
            LiftService? lift = LiftService.Instance;
            if (lift == null || lift.FloorCount < 2)
                return;
            if (lift.FloorIndexOfHeight(sourcePosition.y) == lift.FloorIndexOfHeight(targetPosition.y))
                return;
            if (lift.NearestLanding(sourcePosition, 8f, out Vector3 landing))
                targetPosition = landing;
        }
    }
}
