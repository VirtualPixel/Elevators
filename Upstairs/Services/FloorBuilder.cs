using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace Upstairs.Services
{
    // Spawns a planned floor with the level's own rooms and doors, through the same calls the
    // game's generator uses, so every client builds it whether or not they run the mod.
    internal static class FloorBuilder
    {
        private sealed class Pool
        {
            public readonly List<PrefabRef> L1 = new List<PrefabRef>();
            public readonly List<PrefabRef> L2 = new List<PrefabRef>();
            public readonly List<PrefabRef> L3 = new List<PrefabRef>();
            public int I1, I2, I3, O1, O2, O3;
        }

        public static IEnumerator Build(LevelGenerator lg, Floor floor, float groundTop, float groundBottom, List<string> avoid, PrefabRef? liftPrefab = null, float liftRotation = 0f, int liftSide = -1)
        {
            Level level = lg.Level;
            Pool normal = MakePool(level.ModulesNormal1, level.ModulesNormal2, level.ModulesNormal3, avoid);
            Pool passage = MakePool(level.ModulesPassage1, level.ModulesPassage2, level.ModulesPassage3, avoid);
            Pool deadEnd = MakePool(level.ModulesDeadEnd1, level.ModulesDeadEnd2, level.ModulesDeadEnd3, avoid);
            Pool extraction = MakePool(level.ModulesExtraction1, level.ModulesExtraction2, level.ModulesExtraction3, avoid);
            if (normal.L1.Count == 0)
            {
                Plugin.LogAlways($"[Build] {level.name} has no normal rooms left after AvoidRooms, {floor.Name} skipped");
                yield break;
            }

            var picks = new List<PrefabRef>(floor.Plan.Cells.Count);
            float pickBottom = float.MaxValue;
            float pickTop = float.MinValue;
            foreach (FloorPlan.Cell cell in floor.Plan.Cells)
            {
                if (liftPrefab != null && cell.X == floor.Plan.LiftX && cell.Y == floor.Plan.LiftY)
                {
                    cell.RotationY = liftRotation;
                    picks.Add(liftPrefab);
                    if (ModuleExtents.TryMeasure(liftPrefab.Prefab, out float lb, out float lt))
                    {
                        if (lb < pickBottom) pickBottom = lb;
                        if (lt > pickTop) pickTop = lt;
                    }
                    continue;
                }
                Pool pool = cell.Kind switch
                {
                    FloorPlan.Kind.DeadEnd when cell.Extraction && extraction.L1.Count > 0 => extraction,
                    FloorPlan.Kind.Passage when passage.L1.Count > 0 => passage,
                    FloorPlan.Kind.DeadEnd when deadEnd.L1.Count > 0 => deadEnd,
                    _ => normal
                };
                PrefabRef pick = lg.PickModule(pool.L1, pool.L2, pool.L3, ref pool.I1, ref pool.I2, ref pool.I3, ref pool.O1, ref pool.O2, ref pool.O3);
                picks.Add(pick);
                if (ModuleExtents.TryMeasure(pick.Prefab, out float bottom, out float top))
                {
                    if (bottom < pickBottom) pickBottom = bottom;
                    if (top > pickTop) pickTop = top;
                }
            }
            if (pickBottom == float.MaxValue)
            {
                pickBottom = -30f;
                pickTop = 30f;
            }

            float gap = floor.Above ? FloorGap.Compute(groundTop, pickBottom) : FloorGap.Compute(pickTop, groundBottom);
            if (!floor.Above && gap > 70f)
            {
                Plugin.LogAlways($"[Build] basement would need a {gap:0} m drop, which is past the game's void line. Skipped on {level.name}");
                yield break;
            }
            floor.Y = floor.Above ? gap : -gap;
            floor.PickBottom = pickBottom;
            floor.PickTop = pickTop;

            float w = LevelGenerator.ModuleWidth * LevelGenerator.TileSize;
            int half = lg.LevelWidth / 2;
            bool networked = GameManager.instance.gameMode != 0;
            for (int i = 0; i < floor.Plan.Cells.Count; i++)
            {
                FloorPlan.Cell cell = floor.Plan.Cells[i];
                PrefabRef pick = picks[i];
                var position = new Vector3(cell.X * w - half * w, floor.Y, cell.Y * w + w / 2f);
                Quaternion rotation = Quaternion.Euler(0f, cell.RotationY, 0f);
                GameObject room = networked
                    ? PhotonNetwork.InstantiateRoomObject(pick.ResourcePath, position, rotation, 0)
                    : Object.Instantiate(pick.Prefab, position, rotation);
                room.transform.parent = lg.LevelParent.transform;
                Module module = room.GetComponent<Module>();
                module.GridX = cell.X;
                module.GridY = cell.Y;
                bool lift = cell.X == floor.Plan.LiftX && cell.Y == floor.Plan.LiftY;
                module.ModuleConnectionSet(cell.Top || (lift && liftSide == 0), cell.Bottom || (lift && liftSide == 2), cell.Right || (lift && liftSide == 1), cell.Left || (lift && liftSide == 3), false);
                floor.Modules.Add(module);
                Plugin.LogVerbose($"[Build] {floor.Name} ({cell.X},{cell.Y}) {cell.Kind} rot {cell.RotationY:0} {pick.PrefabName}");
                yield return null;
            }

            foreach (FloorPlan.Door door in floor.Plan.Doors)
            {
                float px = door.X * w - half * w;
                float pz = door.Y * w + w / 2f;
                if (door.RightEdge)
                    lg.SpawnConnectObject(new Vector3(px + w / 2f, floor.Y, pz), new Vector3(0f, 90f, 0f));
                else
                    lg.SpawnConnectObject(new Vector3(px, floor.Y, pz + w / 2f), Vector3.zero);
            }

            lg.ModuleAmount += floor.Plan.Cells.Count;
            Plugin.LogAlways($"[Build] {floor.Name}: {floor.Plan.Cells.Count} rooms, {floor.Plan.Doors.Count} doors at y={floor.Y:0} (ground {groundBottom:0}..{groundTop:0}, rooms {pickBottom:0}..{pickTop:0})");
        }

        private static Pool MakePool(List<PrefabRef> l1, List<PrefabRef> l2, List<PrefabRef> l3, List<string> avoid)
        {
            var pool = new Pool();
            Fill(pool.L1, l1, avoid);
            Fill(pool.L2, l2, avoid);
            Fill(pool.L3, l3, avoid);
            return pool;
        }

        private static void Fill(List<PrefabRef> into, List<PrefabRef> from, List<string> avoid)
        {
            if (from == null)
                return;
            foreach (PrefabRef pr in from)
            {
                if (pr != null && pr.IsValid() && !RoomFilter.Avoids(pr.PrefabName ?? "", avoid))
                    into.Add(pr);
            }
            for (int i = into.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (into[i], into[j]) = (into[j], into[i]);
            }
        }
    }
}
