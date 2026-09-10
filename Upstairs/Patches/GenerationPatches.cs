using System.Collections;
using HarmonyLib;
using UnityEngine;
using Upstairs.Services;

namespace Upstairs.Patches
{
    [HarmonyPatch(typeof(LevelGenerator), "Start")]
    internal static class LevelStartPatch
    {
        private static void Prefix()
        {
            Session.Reset();
            LiftService.Instance?.Clear();
            UpstairsNet.ResetApplied();
            if (RunManager.instance != null)
                // The menu's own generator runs after this; a bad prefab build must not take it down.
                try
                {
                    Elevator.EnsureButtonPrefab();
                }
                catch (System.Exception e)
                {
                    Plugin.Logger.LogError($"[Elevator] button prefab build failed, the elevator will have no buttons: {e}");
                }
        }
    }

    // Before the grid is laid, the ground floor's room count is held to GroundRooms through the
    // generator's own size multiplier; the rooms it gives up are planned onto the other floors.
    [HarmonyPatch(typeof(LevelGenerator), "TileGeneration")]
    internal static class TileGenerationPatch
    {
        private static void Prefix(LevelGenerator __instance)
        {
            if (!SemiFunc.RunIsLevel())
                return;
            __instance.DebugLevelSize = LevelSize.GroundFactor();
            Plugin.LogInfo($"[Plan] level {(RunManager.instance != null ? RunManager.instance.levelsCompleted : -1) + 1}: the game wants {LevelSize.Vanilla()} rooms, ground factor {__instance.DebugLevelSize:0.00}");
        }
    }

    // The grid is final once the game starts placing rooms, so the plan is drawn here.
    [HarmonyPatch(typeof(LevelGenerator), "ModuleGeneration")]
    internal static class ModuleGenerationPatch
    {
        private static void Prefix(LevelGenerator __instance) => Session.Plan(__instance);
    }

    [HarmonyPatch(typeof(LevelGenerator), "SpawnModule")]
    internal static class SpawnModulePatch
    {
        private static void Prefix(int x, int y, Module.Type type) => Session.BeforeSpawn(x, y, type);

        private static void Postfix() => Session.Override = null;
    }

    // Hands the game the lift room for the lift cell; every other pick stays the game's.
    [HarmonyPatch(typeof(LevelGenerator), "PickModule")]
    internal static class PickModulePatch
    {
        private static bool Prefix(ref PrefabRef __result)
        {
            PrefabRef? forced = Session.Override;
            if (forced == null)
                return true;
            Session.Override = null;
            __result = forced;
            return false;
        }
    }

    // Block objects are the last ground floor step, so the new floors go up right after.
    [HarmonyPatch(typeof(LevelGenerator), "GenerateBlockObjects")]
    internal static class BlockObjectsPatch
    {
        private static void Postfix(LevelGenerator __instance, ref IEnumerator __result)
        {
            if (Session.Active && Session.Generator == __instance)
                __result = WithFloors(__instance, __result);
        }

        private static IEnumerator WithFloors(LevelGenerator lg, IEnumerator original)
        {
            while (original.MoveNext())
                yield return original.Current;
            lg.waitingForSubCoroutine = true;
            try
            {
                IEnumerator build = Session.BuildFloors(lg);
                while (build.MoveNext())
                    yield return build.Current;
            }
            finally
            {
                lg.waitingForSubCoroutine = false;
            }
        }
    }

    // Mods that fill the block object slot (Deadstock does) get a blocked door on every wall with
    // no neighbour, which would board up the elevator's wall. That one spot stays open.
    [HarmonyPatch(typeof(LevelGenerator), "SpawnBlockObject")]
    internal static class SpawnBlockObjectPatch
    {
        private static bool Prefix(Vector3 position)
        {
            if (!Session.Active || Session.LiftSide < 0)
                return true;
            Vector3 wall = Session.LiftWallCenter();
            if (Vector3.Distance(new Vector3(position.x, 0f, position.z), new Vector3(wall.x, 0f, wall.z)) > 1f)
                return true;
            Plugin.LogInfo("[Plan] kept the elevator wall free of a block object");
            return false;
        }
    }

    [HarmonyPatch(typeof(LevelGenerator), "EnemySetup")]
    internal static class EnemySetupPatch
    {
        private static void Prefix(LevelGenerator __instance) => Session.KeepEnemiesGrounded(__instance);
    }

    [HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
    internal static class GenerateDonePatch
    {
        private static void Postfix(LevelGenerator __instance)
        {
            if (!__instance.Generated)
                return;
            if (Session.Active)
                LiftService.Instance?.Setup();
            else
                UpstairsNet.TryApply();
        }
    }
}
