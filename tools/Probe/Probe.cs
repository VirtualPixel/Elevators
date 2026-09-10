using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Upstairs.Services;

namespace UpstairsProbe
{
    [BepInPlugin("Vippy.UpstairsProbe", "UpstairsProbe", "0.1.0")]
    [BepInDependency("Vippy.Upstairs")]
    public class Probe : BaseUnityPlugin
    {
        private static string[] levels = { "Museum", "Manor", "Arctic", "Wizard" };

        private ManualLogSource log = null!;
        internal static Level? Forced;

        private void Awake()
        {
            log = Logger;
            string flag = Path.Combine(Paths.ConfigPath, "upstairs-probe.flag");
            if (!File.Exists(flag))
                return;
            string wanted = File.ReadAllText(flag).Trim();
            if (wanted.Length > 0)
                levels = wanted.Split(',');
            File.Delete(flag);
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            new Harmony("Vippy.UpstairsProbe").PatchAll();
            log.LogInfo("[Probe] armed");
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            while (RunManager.instance == null || RunManager.instance.levelCurrent != RunManager.instance.levelMainMenu || MainMenuOpen.instance == null || StatsManager.instance == null)
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(4f);

            RunManager rm = RunManager.instance;
            var original = new List<Level>(rm.levels);
            for (int i = 0; i < levels.Length; i++)
            {
                bool needSite = levels[i].EndsWith("!");
                string levelName = levels[i].TrimEnd('!');
                Level? target = original.Find(l => l.name.Contains(levelName));
                if (target == null)
                {
                    log.LogWarning($"[Probe] no level named like {levelName}");
                    continue;
                }
                Forced = target;
                if (i == 0)
                {
                    SemiFunc.MenuActionSingleplayerGame();
                }
                else
                {
                    // A death sends the game to the arena; from there ChangeLevel would only reload it.
                    rm.gameOver = false;
                    rm.levelCurrent = rm.levelLobby;
                    rm.ChangeLevel(true, false, RunManager.ChangeLevelType.RunLevel);
                }
                yield return new WaitForSecondsRealtime(3f);

                float waited = 0f;
                while (waited < 180f && !(LevelGenerator.Instance != null && LevelGenerator.Instance.Generated && SemiFunc.RunIsLevel()))
                {
                    yield return new WaitForSecondsRealtime(1f);
                    waited += 1f;
                }
                Forced = null;
                if (waited >= 180f)
                {
                    log.LogError($"[Probe] {levels[i]}: level never generated");
                    continue;
                }
                yield return new WaitForSecondsRealtime(6f);
                if (needSite && Session.Site == null)
                {
                    log.LogInfo($"[Probe] {levelName}: no lift site this roll, rolling again");
                    continue;
                }
                Report(levelName);
                yield return Rides(levelName);
            }
            log.LogInfo("[Probe] DONE");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }

        private void Report(string name)
        {
            LevelGenerator lg = LevelGenerator.Instance;
            log.LogInfo($"[Probe] {name}: level={lg.Level.name} active={Session.Active} ground={Session.GroundBottom:0.0}..{Session.GroundTop:0.0} site={(Session.Site != null ? Session.Site.ModuleName : "none")} hint='{Session.Hint}'");
            Module[] modules = Object.FindObjectsOfType<Module>();
            ModuleConnectObject[] doors = Object.FindObjectsOfType<ModuleConnectObject>();
            ValuableObject[] valuables = Object.FindObjectsOfType<ValuableObject>();
            LevelPoint[] points = Object.FindObjectsOfType<LevelPoint>();
            EnemyParent[] enemies = Object.FindObjectsOfType<EnemyParent>();
            NavMeshTriangulation nav = NavMesh.CalculateTriangulation();

            foreach (Floor? floor in new[] { Session.Up, Session.Down })
            {
                if (floor == null)
                    continue;
                int spawned = Count(modules, m => Mathf.Abs(m.transform.position.y - floor.Y) < 1f);
                int spawnedDoors = Count(doors, d => Mathf.Abs(d.transform.position.y - floor.Y) < 1f);
                int loot = Count(valuables, v => floor.Above ? v.transform.position.y > Session.GroundTop : v.transform.position.y < Session.GroundBottom);
                int pts = Count(points, p => floor.Above ? p.transform.position.y > Session.GroundTop : p.transform.position.y < Session.GroundBottom);
                int navVerts = 0;
                foreach (Vector3 v in nav.vertices)
                    if (floor.Above ? v.y > Session.GroundTop : v.y < Session.GroundBottom) navVerts++;
                int monsters = Count(enemies, e => e.Enemy && (floor.Above ? e.Enemy.transform.position.y > Session.GroundTop : e.Enemy.transform.position.y < Session.GroundBottom));
                log.LogInfo($"[Probe] {name} {floor.Name}: y={floor.Y:0} rooms planned={floor.Plan.Cells.Count} spawned={spawned} doors planned={floor.Plan.Doors.Count} spawned={spawnedDoors} loot={loot} points={pts} navVerts={navVerts} monsters={monsters} pick={floor.PickBottom:0}..{floor.PickTop:0}");
            }
            int groundLoot = Count(valuables, v => v.transform.position.y <= Session.GroundTop && v.transform.position.y >= Session.GroundBottom);
            log.LogInfo($"[Probe] {name} ground: modules={Count(modules, m => Mathf.Abs(m.transform.position.y) < 1f)} doors={Count(doors, d => Mathf.Abs(d.transform.position.y) < 1f)} loot={groundLoot} of {valuables.Length} pathPoints={lg.LevelPathPoints.Count} of {points.Length} monsters={enemies.Length}");
            log.LogInfo($"[Probe] {name} pads: {(LiftService.Instance != null ? LiftService.Instance.Describe() : "no lift service")} elevator={Elevator.Built} docks={Elevator.DockCount}");
            foreach (string pad in new[] { "ground", "upstairs", "basement" })
            {
                if (LiftService.Instance != null && LiftService.Instance.TryGetPad(pad, out Vector3 pos))
                {
                    bool onNav = NavMesh.SamplePosition(pos, out NavMeshHit hit, 0.6f, NavMesh.AllAreas);
                    log.LogInfo($"[Probe] {name} pad {pad} onNavMesh={onNav} navDist={(onNav ? hit.distance : -1f):0.00}");
                }
            }
        }

        private IEnumerator Rides(string name)
        {
            LiftService? lift = LiftService.Instance;
            PlayerAvatar me = PlayerAvatar.instance;
            if (lift == null || me == null || !lift.TryGetPad("ground", out Vector3 ground) || !lift.TryGetPad("upstairs", out Vector3 up))
            {
                log.LogWarning($"[Probe] {name}: no lift pads, ride test skipped");
                yield break;
            }

            if (Elevator.Built)
            {
                yield return ElevatorRides(name, me, ground, up);
                yield break;
            }
            Place(me, ground + Vector3.up * 0.2f);
            yield return new WaitForSecondsRealtime(3f);
            Verdict(name, "RIDE UP", me.transform.position, up);

            yield return new WaitForSecondsRealtime(4.5f);
            Place(me, up + Vector3.right * 4f + Vector3.up * 0.2f);
            yield return new WaitForSecondsRealtime(0.5f);
            Place(me, up + Vector3.up * 0.2f);
            yield return new WaitForSecondsRealtime(3f);
            Verdict(name, "RIDE DOWN", me.transform.position, ground);

            if (Session.Up != null)
            {
                yield return new WaitForSecondsRealtime(4.5f);
                // Drop onto whatever roof sits under the upper floor, a few metres up, and expect the rescue.
                var from = new Vector3(ground.x, Session.GroundTop + 2f, ground.z);
                float roofY = Physics.Raycast(from, Vector3.down, out RaycastHit roof, 200f) ? roof.point.y : ground.y;
                Place(me, new Vector3(ground.x, roofY + 4f, ground.z));
                log.LogInfo($"[Probe] {name} void test: dropped onto roof at y={roofY:0.0}");
                yield return new WaitForSecondsRealtime(5f);
                Verdict(name, "VOID RESCUE", me.transform.position, ground);
            }

            if (lift.TryGetPad("basement", out Vector3 basement))
            {
                yield return new WaitForSecondsRealtime(4.5f);
                lift.ProbeRide(me, "basement");
                yield return new WaitForSecondsRealtime(3f);
                Verdict(name, "BASEMENT DROP", me.transform.position, basement);
                yield return new WaitForSecondsRealtime(4.5f);
                Place(me, basement + Vector3.right * 4f + Vector3.up * 0.2f);
                yield return new WaitForSecondsRealtime(0.5f);
                Place(me, basement + Vector3.up * 0.2f);
                yield return new WaitForSecondsRealtime(3f);
                Verdict(name, "BASEMENT UP", me.transform.position, ground);
            }
        }

        // Stand in the car, press up, get carried; then press down, get carried back; then the void check.
        private IEnumerator ElevatorRides(string name, PlayerAvatar me, Vector3 ground, Vector3 up)
        {
            Place(me, ground + Vector3.up * 0.2f);
            yield return new WaitForSecondsRealtime(1f);
            int upDock = DockNamed("upstairs");
            log.LogInfo($"[Probe] {name} pressing up (dock {upDock}), inside={Elevator.IsInside(me.transform.position)}");
            UpstairsNet.RequestRide(upDock);
            float waited = 0f;
            while (Elevator.Moving && waited < 30f) { yield return new WaitForSecondsRealtime(0.5f); waited += 0.5f; }
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR UP", me.transform.position, up);
            UpstairsNet.RequestRide(DockNamed("ground"));
            waited = 0f;
            while (Elevator.Moving && waited < 30f) { yield return new WaitForSecondsRealtime(0.5f); waited += 0.5f; }
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR DOWN", me.transform.position, ground);
            if (Session.Up != null)
            {
                var from = new Vector3(ground.x + 6f, Session.GroundTop + 2f, ground.z);
                float roofY = Physics.Raycast(from, Vector3.down, out RaycastHit roof, 200f) ? roof.point.y : ground.y;
                Place(me, new Vector3(from.x, roofY + 4f, from.z));
                yield return new WaitForSecondsRealtime(6f);
                bool inRoom = Physics.CheckSphere(me.transform.position + Vector3.up * 0.6f, 0.25f, LayerMask.GetMask("RoomVolume"), QueryTriggerInteraction.Collide);
                if (inRoom)
                    log.LogInfo($"[Probe] {name} VOID RESCUE n/a, the drop landed inside a room at {me.transform.position}");
                else
                    Verdict(name, "VOID RESCUE", me.transform.position, ground);
            }
        }

        private static int DockNamed(string name)
        {
            LiftService? lift = LiftService.Instance;
            if (lift == null) return -1;
            string[] list = lift.Describe().Split('|');
            for (int i = 0; i < list.Length; i++)
                if (list[i].Trim().StartsWith(name + "@")) return i;
            return -1;
        }

        // A tumbling player is carried by the tumble body, so Spawn alone would snap straight back.
        private static void Place(PlayerAvatar me, Vector3 pos)
        {
            if (me.isTumbling && me.tumble)
            {
                me.tumble.physGrabObject.Teleport(pos, Quaternion.identity);
                me.tumble.physGrabObject.rb.velocity = Vector3.zero;
                me.tumble.physGrabObject.rb.angularVelocity = Vector3.zero;
                return;
            }
            me.Spawn(pos, Quaternion.identity);
        }

        private void Verdict(string name, string what, Vector3 got, Vector3 want)
        {
            bool ok = Mathf.Abs(got.y - want.y) < 3f && Vector2.Distance(new Vector2(got.x, got.z), new Vector2(want.x, want.z)) < 6f;
            log.LogInfo($"[Probe] {name} {what} {(ok ? "ok" : "FAIL")} at={got} want={want}");
        }

        private static int Count<T>(T[] items, System.Predicate<T> test)
        {
            int n = 0;
            foreach (T item in items)
                if (item != null && test(item)) n++;
            return n;
        }
    }

    [HarmonyPatch(typeof(RunManager), "SetRunLevel")]
    [HarmonyPriority(Priority.First)]
    internal static class ForceLevelPatch
    {
        private static bool Prefix(RunManager __instance)
        {
            if (Probe.Forced == null)
                return true;
            __instance.levelCurrent = Probe.Forced;
            return false;
        }
    }
}
