using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // Host-side state for the level being generated: the plans, the lift cell, and the
    // measured heights everything else keys off. Reset every time a level starts.
    internal static class Session
    {
        public static bool Active { get; private set; }
        public static LevelGenerator? Generator { get; private set; }
        public static FloorPlan? UpPlan { get; private set; }
        public static FloorPlan? DownPlan { get; private set; }
        public static Floor? Up { get; private set; }
        public static Floor? Down { get; private set; }
        public static LiftSite? Site { get; private set; }
        public static int LiftX { get; private set; }
        public static int LiftY { get; private set; }
        public static float GroundTop { get; private set; }
        public static float GroundBottom { get; private set; }
        // The lift cell wall the elevator stands in: 0 top (+z), 1 right (+x), 2 bottom (-z), 3 left (-x). -1 when the car rides a real shaft.
        public static int LiftSide { get; private set; } = -1;
        public static string Hint { get; private set; } = "";
        // What a client knows from the host's room property.
        private static bool remoteUp;
        private static bool remoteDown;
        public static bool KnowsFloors => Active || remoteUp || remoteDown;

        // Set just before the game's PickModule runs for the lift cell, consumed by it.
        public static PrefabRef? Override;

        private static PrefabRef? sitePrefab;
        private static Module.Type liftType;
        // One of the ground's extraction rooms moves to a new floor: the ground spawn of it is
        // swapped for a plain dead end, and a dead end up there becomes the extraction room.
        private static bool moveExtraction;
        private static int extractionsSeen;
        private static int extractionsOnGround;
        private static int roomsFromTruck;
        private static bool? liftAxisVertical;
        private static bool[,]? groundGrid;
        private static int cellCount;
        private static int passageCap;
        private static System.Random? planRng;

        public static void Reset()
        {
            Active = false;
            Generator = null;
            UpPlan = null;
            DownPlan = null;
            Up = null;
            Down = null;
            Site = null;
            sitePrefab = null;
            Override = null;
            liftAxisVertical = null;
            LiftSide = -1;
            Hint = "";
            GroundTop = 0f;
            GroundBottom = 0f;
            remoteUp = false;
            remoteDown = false;
            LootPass.Reset();
        }

        public static void ApplyRemote(LiftPayload payload)
        {
            GroundTop = payload.GroundTop;
            GroundBottom = payload.GroundBottom;
            remoteUp = payload.HasUp;
            remoteDown = payload.HasDown;
        }

        public static bool ShouldRun(LevelGenerator lg)
        {
            return PluginConfig.Enabled.Value
                && (PluginConfig.UpperFloor.Value || PluginConfig.Basement.Value)
                && SemiFunc.IsMasterClientOrSingleplayer()
                && SemiFunc.RunIsLevel()
                && lg.Level != null
                && lg.Level.ModulesNormal1 != null
                && lg.Level.ModulesNormal1.Count > 0
                && lg.LevelGrid != null
                && lg.DebugModule == null;
        }

        public static bool IsOnNewFloor(float y)
        {
            return ((Up != null || remoteUp) && y > GroundTop) || ((Down != null || remoteDown) && y < GroundBottom);
        }

        // Runs when the game is about to place the ground floor rooms, so the grid is final.
        public static void Plan(LevelGenerator lg)
        {
            Reset();
            if (!ShouldRun(lg))
                return;
            Generator = lg;

            int width = lg.LevelWidth;
            int height = lg.LevelHeight;
            var ground = new bool[width, height];
            int activeCells = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    ground[x, y] = lg.LevelGrid[x, y].active;
                    if (ground[x, y])
                        activeCells++;
                }
            }

            var rng = new System.Random(Random.Range(int.MinValue, int.MaxValue));
            Site = LiftSites.For(lg.Level.name);
            sitePrefab = Site != null ? FindPrefab(lg.Level, Site) : null;
            if (sitePrefab == null)
                Site = null;
            if (!ChooseLiftCell(lg, ground, rng))
            {
                Plugin.LogAlways($"[Plan] {lg.Level.name}: no room to hang a lift on, no extra floors this level");
                return;
            }

            int count = FloorGap.CellCount(activeCells, PluginConfig.FloorSize.Value);
            int passageMax = lg.Level.PassageMaxAmount;
            groundGrid = ground;
            cellCount = count;
            passageCap = passageMax;
            planRng = rng;
            bool? axis = Site != null && Site.Shaft ? liftAxisVertical : null;
            if (PluginConfig.UpperFloor.Value)
                UpPlan = FloorPlan.Build(ground, LiftX, LiftY, count, passageMax, rng, axis, LiftSide);
            if (PluginConfig.Basement.Value)
                DownPlan = FloorPlan.Build(ground, LiftX, LiftY, count, passageMax, rng, axis, LiftSide);
            roomsFromTruck = RoomsFromTruck(ground, width / 2, 0, LiftX, LiftY);
            Hint = Site?.Hint ?? FallbackHint();
            Active = true;
            PlanExtraction(lg, rng);
            Plugin.LogInfo($"[Plan] {lg.Level.name}: ground {activeCells} cells, lift at ({LiftX},{LiftY}) {(Site != null ? Site.ModuleName : "plain room")}, up {UpPlan?.Cells.Count ?? 0}, down {DownPlan?.Cells.Count ?? 0}");
        }

        // With two or more extraction points, one goes upstairs or down: a dead-end cell on that
        // floor takes an extraction room and the last ground extraction spawn becomes a plain
        // dead end, so the count the round wants stays the game's own.
        private static void PlanExtraction(LevelGenerator lg, System.Random rng)
        {
            moveExtraction = false;
            extractionsSeen = 0;
            extractionsOnGround = lg.ExtractionAmount;
            if (!PluginConfig.ExtractionOnNewFloors.Value || extractionsOnGround < 2)
                return;
            // The upper floor first: a basement can still be dropped at build time when the level
            // runs too deep, and it would take the point with it.
            var floors = new List<FloorPlan>();
            if (UpPlan != null) floors.Add(UpPlan);
            if (DownPlan != null) floors.Add(DownPlan);
            foreach (FloorPlan plan in floors)
            {
                var leaves = plan.Cells.FindAll(c => c.Kind == FloorPlan.Kind.DeadEnd && !(c.X == plan.LiftX && c.Y == plan.LiftY));
                if (leaves.Count == 0)
                    continue;
                leaves[rng.Next(leaves.Count)].Extraction = true;
                moveExtraction = true;
                Plugin.LogInfo($"[Plan] one of {extractionsOnGround} extraction points goes {(plan == UpPlan ? "upstairs" : "to the basement")}");
                return;
            }
        }

        private static PrefabRef? PlainDeadEnd(LevelGenerator lg)
        {
            List<string> avoid = RoomFilter.Parse(PluginConfig.AvoidRooms.Value);
            var options = new List<PrefabRef>();
            foreach (List<PrefabRef> list in new[] { lg.Level.ModulesDeadEnd1, lg.Level.ModulesDeadEnd2, lg.Level.ModulesDeadEnd3 })
            {
                if (list == null) continue;
                foreach (PrefabRef pr in list)
                    if (pr != null && pr.IsValid() && !RoomFilter.Avoids(pr.PrefabName, avoid)) options.Add(pr);
            }
            return options.Count > 0 ? options[UnityEngine.Random.Range(0, options.Count)] : null;
        }

        public static void BeforeSpawn(int x, int y, Module.Type type)
        {
            if (Active && moveExtraction && type == Module.Type.Extraction)
            {
                extractionsSeen++;
                if (extractionsSeen == extractionsOnGround)
                {
                    PrefabRef? plain = PlainDeadEnd(LevelGenerator.Instance);
                    if (plain != null)
                    {
                        Override = plain;
                        Plugin.LogInfo($"[Plan] ground extraction at ({x},{y}) becomes {plain.PrefabName}, its point is on a new floor");
                        return;
                    }
                    moveExtraction = false;
                    foreach (FloorPlan? plan in new[] { UpPlan, DownPlan })
                        plan?.Cells.ForEach(c => c.Extraction = false);
                }
            }
            if (!Active || sitePrefab == null || x != LiftX || y != LiftY)
                return;
            if (type == liftType)
            {
                Override = sitePrefab;
                return;
            }
            Plugin.LogInfo($"[Plan] game wanted a {type} room at the lift cell, so the lift lands in a plain room instead");
            Site = null;
            sitePrefab = null;
            Hint = FallbackHint();
            if (UpPlan != null) UpPlan = null;
            if (DownPlan != null) DownPlan = null;
            Active = false;
        }

        public static IEnumerator BuildFloors(LevelGenerator lg)
        {
            MeasureGround();
            SettleLiftSide();
            List<string> avoid = RoomFilter.Parse(PluginConfig.AvoidRooms.Value);
            // The same lift room stands on every floor, turned the way the ground one landed: a shaft
            // site's elevator room, or otherwise whatever room the game put at the ground lift cell.
            // A random room there once left a dead end with no floor behind the opened wall.
            PrefabRef? liftPrefab = null;
            float liftRotation = 0f;
            Module? groundLift = GroundLiftModule();
            if (groundLift != null)
            {
                if (Site != null && Site.Shaft && sitePrefab != null && groundLift.name.StartsWith(Site.ModuleName))
                    liftPrefab = sitePrefab;
                else
                    liftPrefab = PrefabOf(lg.Level, groundLift.name);
                liftRotation = groundLift.transform.eulerAngles.y;
                Plugin.LogInfo($"[Plan] lift room on every floor: {(liftPrefab != null ? liftPrefab.PrefabName : "none found for " + groundLift.name)} rot {liftRotation:0}");
            }
            // Each floor is published before its rooms spawn, so the rooms' own setup (room volumes,
            // the map) already knows which floor they belong to.
            if (UpPlan != null)
            {
                Up = new Floor(UpPlan, above: true);
                SpanUnder(UpPlan, out float top, out float bottom);
                IEnumerator build = FloorBuilder.Build(lg, Up, top, bottom, avoid, liftPrefab, liftRotation, LiftSide);
                while (build.MoveNext())
                    yield return build.Current;
                if (Up.Modules.Count == 0)
                    Up = null;
            }
            if (DownPlan != null)
            {
                Down = new Floor(DownPlan, above: false);
                SpanUnder(DownPlan, out float top, out float bottom);
                IEnumerator build = FloorBuilder.Build(lg, Down, top, bottom, avoid, liftPrefab, liftRotation, LiftSide);
                while (build.MoveNext())
                    yield return build.Current;
                if (Down.Modules.Count == 0)
                    Down = null;
            }
            if (Up == null && Down == null)
                Active = false;
        }

        // Monsters pick their spawn and roam targets from this list, so dropping the new
        // floors' points keeps them downstairs.
        public static void KeepEnemiesGrounded(LevelGenerator lg)
        {
            if (!Active || PluginConfig.EnemiesOnNewFloors.Value)
                return;
            int removed = lg.LevelPathPoints.RemoveAll(p => p && IsOnNewFloor(p.transform.position.y));
            Plugin.LogInfo($"[Plan] kept monsters on the ground floor, {removed} path points off the new floors");
        }

        // The room list entry a spawned module came from, by its name.
        private static PrefabRef? PrefabOf(Level level, string moduleName)
        {
            string wanted = moduleName.Replace("(Clone)", "").Trim();
            foreach (List<PrefabRef> list in new[] { level.ModulesNormal1, level.ModulesNormal2, level.ModulesNormal3, level.ModulesPassage1, level.ModulesPassage2, level.ModulesPassage3, level.ModulesDeadEnd1, level.ModulesDeadEnd2, level.ModulesDeadEnd3 })
            {
                if (list == null)
                    continue;
                foreach (PrefabRef pr in list)
                {
                    if (pr != null && pr.IsValid() && (pr.PrefabName == wanted || (pr.ResourcePath ?? "").EndsWith(wanted)))
                        return pr;
                }
            }
            return null;
        }

        public static Module? GroundLiftModule()
        {
            foreach (Module module in Object.FindObjectsOfType<Module>())
            {
                if (module.GridX == LiftX && module.GridY == LiftY && !module.StartRoom && Mathf.Abs(module.transform.position.y) < 0.5f)
                    return module;
            }
            return null;
        }

        // The gap only has to clear the ground rooms a floor actually sits on, not the tallest room
        // anywhere in the level. Cuts the climb by half on most seeds.
        private static void SpanUnder(FloorPlan plan, out float top, out float bottom)
        {
            top = float.MinValue;
            bottom = float.MaxValue;
            foreach (Module module in Object.FindObjectsOfType<Module>())
            {
                if (module.StartRoom || Mathf.Abs(module.transform.position.y) > 0.5f || plan.At(module.GridX, module.GridY) == null)
                    continue;
                if (!ModuleExtents.TryMeasure(module.gameObject, out float b, out float t))
                    continue;
                if (t > top) top = t;
                if (b < bottom) bottom = b;
            }
            if (top == float.MinValue)
            {
                top = GroundTop;
                bottom = GroundBottom;
            }
        }

        private static void MeasureGround()
        {
            float top = float.MinValue;
            float bottom = float.MaxValue;
            foreach (Module module in Object.FindObjectsOfType<Module>())
            {
                if (Mathf.Abs(module.transform.position.y) > 0.5f)
                    continue;
                if (!ModuleExtents.TryMeasure(module.gameObject, out float b, out float t))
                    continue;
                if (t > top) top = t;
                if (b < bottom) bottom = b;
            }
            GroundTop = top == float.MinValue ? 30f : top;
            GroundBottom = bottom == float.MaxValue ? -30f : bottom;
        }

        private static bool ChooseLiftCell(LevelGenerator lg, bool[,] ground, System.Random rng)
        {
            int width = lg.LevelWidth;
            int height = lg.LevelHeight;
            var straight = new List<(int, int)>();
            var plain = new List<(int, int)>();
            var deadEnds = new List<(int, int)>();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    LevelGenerator.Tile tile = lg.LevelGrid[x, y];
                    if (!tile.active || tile.first)
                        continue;
                    if (tile.type == Module.Type.DeadEnd)
                    {
                        deadEnds.Add((x, y));
                        continue;
                    }
                    if (tile.type != Module.Type.Normal)
                        continue;
                    bool top = Has(ground, x, y + 1), bottom = Has(ground, x, y - 1), right = Has(ground, x + 1, y), left = Has(ground, x - 1, y);
                    bool vertical = top && bottom && !left && !right;
                    bool horizontal = left && right && !top && !bottom;
                    if (vertical || horizontal)
                        straight.Add((x, y));
                    else
                        plain.Add((x, y));
                }
            }

            if (Site != null)
            {
                List<(int, int)> wanted = Site.Kind switch
                {
                    FloorPlan.Kind.Passage => straight,
                    FloorPlan.Kind.DeadEnd => deadEnds,
                    _ => plain
                };
                if (wanted.Count > 0)
                {
                    // Straight cells go first in the game's own scan order, which is also where its
                    // passage budget is most likely to still be open.
                    (LiftX, LiftY) = Site.Kind == FloorPlan.Kind.Passage ? wanted[0] : wanted[rng.Next(wanted.Count)];
                    if (Site.Kind == FloorPlan.Kind.Passage)
                        liftAxisVertical = Has(ground, LiftX, LiftY + 1) && Has(ground, LiftX, LiftY - 1);
                    liftType = Site.Kind switch
                    {
                        FloorPlan.Kind.Passage => Module.Type.Passage,
                        FloorPlan.Kind.DeadEnd => Module.Type.DeadEnd,
                        _ => Module.Type.Normal
                    };
                    return true;
                }
                Site = null;
                sitePrefab = null;
            }

            // A plain room with a wall nobody is behind: the elevator goes in that wall.
            var withFreeSide = new List<(int, int)>();
            foreach ((int px, int py) in plain)
            {
                if (FreeSide(ground, px, py, rng) >= 0)
                    withFreeSide.Add((px, py));
            }
            if (withFreeSide.Count > 0)
            {
                (LiftX, LiftY) = withFreeSide[rng.Next(withFreeSide.Count)];
                LiftSide = FreeSide(ground, LiftX, LiftY, rng);
                liftType = Module.Type.Normal;
                return true;
            }
            if (plain.Count > 0)
            {
                (LiftX, LiftY) = plain[rng.Next(plain.Count)];
                liftType = Module.Type.Normal;
                return true;
            }
            if (straight.Count > 0)
            {
                (LiftX, LiftY) = straight[rng.Next(straight.Count)];
                liftType = Module.Type.Normal;
                return true;
            }
            return false;
        }

        // The space beyond the opened wall has to be empty for the car to stand there. Some levels
        // keep terrain or outer geometry behind their walls, so once the ground floor exists the
        // chosen side is checked and swapped for another free one if something is in the way.
        private static void SettleLiftSide()
        {
            if (LiftSide < 0 || groundGrid == null)
                return;
            Module? lift = GroundLiftModule();
            if (lift == null)
                return;
            if (Clear(lift.transform.position, LiftSide, out string blocker))
                return;
            Plugin.LogInfo($"[Plan] side {LiftSide} of the lift room is blocked by {blocker}, looking for another");
            int x = LiftX, y = LiftY;
            var sides = new List<int>();
            if (!Has(groundGrid, x, y + 1)) sides.Add(0);
            if (!Has(groundGrid, x + 1, y)) sides.Add(1);
            if (y > 0 && !Has(groundGrid, x, y - 1)) sides.Add(2);
            if (!Has(groundGrid, x - 1, y)) sides.Add(3);
            int chosen = -1;
            foreach (int side in sides)
            {
                if (side != LiftSide && Clear(lift.transform.position, side, out _))
                {
                    chosen = side;
                    break;
                }
            }
            if (chosen < 0)
            {
                Plugin.LogAlways("[Plan] every free wall of the lift room is blocked, the elevator stands in the room instead");
                LiftSide = -1;
            }
            else
            {
                LiftSide = chosen;
            }
            // Re-wire the room with the game's own neighbour flags; the patch adds the new side.
            lift.ModuleConnectionSet(Has(groundGrid, x, y + 1), Has(groundGrid, x, y - 1) || y == 0, Has(groundGrid, x + 1, y), Has(groundGrid, x - 1, y), y == 0 && x == groundGrid.GetLength(0) / 2);
            if (planRng != null)
            {
                bool? axis = Site != null && Site.Shaft ? liftAxisVertical : null;
                if (UpPlan != null) UpPlan = FloorPlan.Build(groundGrid, LiftX, LiftY, cellCount, passageCap, planRng, axis, LiftSide);
                if (DownPlan != null) DownPlan = FloorPlan.Build(groundGrid, LiftX, LiftY, cellCount, passageCap, planRng, axis, LiftSide);
            }
        }

        private static bool Clear(Vector3 center, int side, out string blocker)
        {
            Vector3 dir = SideDirection(side);
            float half = LevelGenerator.ModuleWidth * LevelGenerator.TileSize * 0.5f;
            Vector3 probe = center + dir * (half + 2.0f) + Vector3.up * 2.2f;
            Collider[] hits = Physics.OverlapBox(probe, new Vector3(1.2f, 1.8f, 1.2f), Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            blocker = "";
            foreach (Collider hit in hits)
            {
                // Solid geometry, or a death pit's trigger: both mean the car cannot stand here.
                if (!hit.isTrigger && hit.gameObject.layer == LayerMask.NameToLayer("Default") || hit.GetComponentInParent<HurtCollider>() != null)
                {
                    blocker = hit.name;
                    return false;
                }
            }
            return true;
        }

        // A side of the cell with no room behind it. The truck side of the first row is never free.
        private static int FreeSide(bool[,] ground, int x, int y, System.Random rng)
        {
            var free = new List<int>();
            if (!Has(ground, x, y + 1)) free.Add(0);
            if (!Has(ground, x + 1, y)) free.Add(1);
            if (y > 0 && !Has(ground, x, y - 1)) free.Add(2);
            if (!Has(ground, x - 1, y)) free.Add(3);
            return free.Count == 0 ? -1 : free[rng.Next(free.Count)];
        }

        // World spot of the lift room's opened wall midpoint on the ground floor.
        public static Vector3 LiftWallCenter()
        {
            float w = LevelGenerator.ModuleWidth * LevelGenerator.TileSize;
            int half = Generator != null ? Generator.LevelWidth / 2 : 0;
            var center = new Vector3(LiftX * w - half * w, 0f, LiftY * w + w / 2f);
            return center + SideDirection(LiftSide) * (w / 2f);
        }

        public static Vector3 SideDirection(int side)
        {
            return side switch { 0 => Vector3.forward, 1 => Vector3.right, 2 => Vector3.back, _ => Vector3.left };
        }

        public static bool IsGroundLiftModule(Module module)
        {
            return Active && module.GridX == LiftX && module.GridY == LiftY && !module.StartRoom && Mathf.Abs(module.transform.position.y) < 0.5f;
        }

        private static bool Has(bool[,] ground, int x, int y)
        {
            return x >= 0 && y >= 0 && x < ground.GetLength(0) && y < ground.GetLength(1) && ground[x, y];
        }

        private static PrefabRef? FindPrefab(Level level, LiftSite site)
        {
            IEnumerable<List<PrefabRef>> lists = site.Kind switch
            {
                FloorPlan.Kind.Passage => new[] { level.ModulesPassage1, level.ModulesPassage2, level.ModulesPassage3 },
                FloorPlan.Kind.DeadEnd => new[] { level.ModulesDeadEnd1, level.ModulesDeadEnd2, level.ModulesDeadEnd3 },
                _ => new[] { level.ModulesNormal1, level.ModulesNormal2, level.ModulesNormal3 }
            };
            foreach (List<PrefabRef> list in lists)
            {
                if (list == null)
                    continue;
                foreach (PrefabRef pr in list)
                {
                    if (pr != null && pr.IsValid() && (pr.PrefabName == site.ModuleName || (pr.ResourcePath ?? "").EndsWith(site.ModuleName)))
                        return pr;
                }
            }
            return null;
        }

        private static int RoomsFromTruck(bool[,] ground, int sx, int sy, int tx, int ty)
        {
            int width = ground.GetLength(0);
            int height = ground.GetLength(1);
            var dist = new int[width, height];
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    dist[x, y] = -1;
            var queue = new Queue<(int, int)>();
            queue.Enqueue((sx, sy));
            dist[sx, sy] = 0;
            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                if (x == tx && y == ty)
                    return dist[x, y];
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (!Has(ground, nx, ny) || dist[nx, ny] >= 0)
                        continue;
                    dist[nx, ny] = dist[x, y] + 1;
                    queue.Enqueue((nx, ny));
                }
            }
            return -1;
        }

        private static string FallbackHint()
        {
            return roomsFromTruck <= 0 ? "in the first room past the truck" : $"about {roomsFromTruck + 1} rooms in from the truck";
        }
    }
}
