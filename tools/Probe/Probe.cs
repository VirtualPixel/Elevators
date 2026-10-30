using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Elevators.Services;
using Elevators.Configuration;

namespace ElevatorsProbe
{
    [BepInPlugin("Vippy.ElevatorsProbe", "ElevatorsProbe", "0.1.0")]
    [BepInDependency("Vippy.Elevators")]
    public class Probe : BaseUnityPlugin
    {
        private static string[] levels = { "Museum", "Manor", "Arctic", "Wizard" };

        private ManualLogSource log = null!;
        internal static Level? Forced;
        // A flag entry reads Level[@Room][:Monster][!]: roll again until the ground lift room's
        // name contains Room, summon Monster for the monster tests, and ! rolls until a lift site.
        private static string? wantMonster;
        internal static string? WantRoom;

        private void Awake()
        {
            log = Logger;
            string flag = Path.Combine(Paths.ConfigPath, "elevators-probe.flag");
            if (!File.Exists(flag))
                return;
            string wanted = File.ReadAllText(flag).Trim();
            if (wanted.Length > 0)
                levels = wanted.Split(',');
            File.Delete(flag);
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            new Harmony("Vippy.ElevatorsProbe").PatchAll();
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
                string spec = levels[i].TrimEnd('!');
                wantMonster = null;
                int colon = spec.IndexOf(':');
                if (colon >= 0) { wantMonster = spec.Substring(colon + 1); spec = spec.Substring(0, colon); }
                string? wantRoom = null;
                int at = spec.IndexOf('@');
                if (at >= 0) { wantRoom = spec.Substring(at + 1); spec = spec.Substring(0, at); }
                string levelName = spec;
                WantRoom = wantRoom;
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
                WantRoom = null;
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
                if (wantRoom != null)
                {
                    Module? liftRoom = Session.GroundLiftModule();
                    string roomName = liftRoom != null ? liftRoom.name : "none";
                    if (!roomName.Contains(wantRoom))
                    {
                        log.LogInfo($"[Probe] {levelName}: lift room is {roomName}, not {wantRoom}, rolling again");
                        continue;
                    }
                }
                // Monsters hear the elevator now; the tester stands in it and must survive that.
                if (PlayerAvatar.instance != null && PlayerAvatar.instance.playerHealth != null)
                    PlayerAvatar.instance.playerHealth.godMode = true;
                Report(levelName);
                OpeningScan(levelName);
                yield return MapCheck(levelName);
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
            ExtractionPoint[] points2 = Object.FindObjectsOfType<ExtractionPoint>();
            int highPoints = Count(points2, e => e.transform.position.y > Session.GroundTop);
            int lowPoints = Count(points2, e => e.transform.position.y < Session.GroundBottom);
            log.LogInfo($"[Probe] {name} extraction points: {points2.Length} total, {highPoints} upstairs, {lowPoints} in the basement, round wants {(RoundDirector.instance != null ? RoundDirector.instance.extractionPoints : -1)}");
            foreach (ExtractionPoint e in points2)
            {
                Module? m = e.GetComponentInParent<Module>();
                log.LogInfo($"[Probe] {name} extraction point at {e.transform.position} in {(m != null ? m.name : e.transform.root.name)}");
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

        // Doorways on the new floors that lead to nothing, and dock landings with a way out or
        // no floor: the holes a player falls through.
        private void OpeningScan(string name)
        {
            int rooms = LayerMask.GetMask("RoomVolume");
            int found = 0;
            foreach (ModuleConnectObject door in Object.FindObjectsOfType<ModuleConnectObject>())
            {
                float y = door.transform.position.y;
                if (y <= Session.GroundTop && y >= Session.GroundBottom) continue;
                Vector3 origin = door.transform.position + Vector3.up * 1.2f;
                foreach (Vector3 dir in new[] { door.transform.forward, -door.transform.forward })
                {
                    Vector3 beyond = origin + dir * 2.5f;
                    bool room = Physics.CheckSphere(beyond, 0.3f, rooms, QueryTriggerInteraction.Collide);
                    bool blocked = Physics.Raycast(origin, dir, 2.5f, ~0, QueryTriggerInteraction.Ignore);
                    if (!room && !blocked)
                    {
                        found++;
                        log.LogWarning($"[Probe] {name} OPEN DOORWAY at {door.transform.position} toward {dir} ({door.name})");
                    }
                }
            }
            Module[] modules = Object.FindObjectsOfType<Module>();
            foreach ((Vector3 pos, float yaw, string dock) in Elevator.DockList())
            {
                Vector3 landing = pos + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, 0f, -2.5f);
                Module? nearest = null;
                float nearestD = 99f;
                foreach (Module m in modules)
                {
                    float d = Vector3.Distance(m.transform.position, pos);
                    if (Mathf.Abs(m.transform.position.y - pos.y) < 6f && d < nearestD) { nearestD = d; nearest = m; }
                }
                bool landingFloor = Physics.Raycast(landing + Vector3.up * 0.5f, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                Vector3 real = Elevator.LandingOf(dock);
                bool realFloor = Physics.Raycast(real + Vector3.up * 0.5f, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                log.LogInfo($"[Probe] {name} dock {dock} at {pos} yaw {yaw:0} roomSide {landing} floor={landingFloor} padLanding {real} floor={realFloor} module={(nearest != null ? nearest.name + " at " + nearest.transform.position + " yaw " + nearest.transform.eulerAngles.y.ToString("0") : "none")} dist={nearestD:0.0}");
                for (int i = 0; i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, 45f * i, 0f) * Vector3.forward;
                    foreach (float h in new[] { 0.6f, 1.5f })
                    {
                        Vector3 origin = landing + Vector3.up * h;
                        if (Physics.Raycast(origin, dir, 5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                        Vector3 end = origin + dir * 5f;
                        if (Physics.CheckSphere(end, 0.3f, rooms, QueryTriggerInteraction.Collide)) continue;
                        found++;
                        log.LogWarning($"[Probe] {name} OPEN at dock {dock} from {landing} dir {i * 45} h {h}");
                    }
                }
                for (int gx = -1; gx <= 1; gx++)
                for (int gz = -1; gz <= 1; gz++)
                {
                    Vector3 spot = landing + new Vector3(gx, 0.5f, gz);
                    if (!Physics.Raycast(spot, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        found++;
                        log.LogWarning($"[Probe] {name} NO FLOOR near dock {dock} at {spot}");
                    }
                }
            }
            foreach (Transform t in Object.FindObjectsOfType<Transform>())
            {
                // Landing doors and shaft pieces share the name; only a mount with a call slot counts.
                if (t.name != "Elevators dock" || !HasChild(t, "slot Call"))
                    continue;
                Transform? button = null;
                foreach (Elevator.LiftButton b in Object.FindObjectsOfType<Elevator.LiftButton>())
                    if (b.Kind == Elevator.ButtonKind.Call && b.transform.IsChildOf(t)) { button = b.transform; break; }
                Vector3 at = button != null ? button.position : t.position + Vector3.up * 1.18f;
                // Anything in the space just in front of the button that isn't the button's own mount.
                string what = "";
                foreach (Collider c in Physics.OverlapSphere(at - t.forward * 0.14f, 0.08f, ~0, QueryTriggerInteraction.Ignore))
                    if (!c.transform.IsChildOf(t)) what += c.name + ",";
                Vector3 viewer = at - t.forward * 1.2f;
                bool seen = !Physics.Linecast(viewer, at - t.forward * 0.1f, out RaycastHit block, ~0, QueryTriggerInteraction.Ignore);
                bool ok = button != null && what.Length == 0 && seen;
                if (!ok) found++;
                log.LogInfo($"[Probe] {name} CALL BUTTON {(ok ? "ok" : "BLOCKED")} {(t.Find("post") != null ? "post" : "wall")} at {at} attached={button != null} inFront={what} view={(seen ? "clear" : block.collider.name)}");
            }
            foreach (Transform t in Object.FindObjectsOfType<Transform>())
            {
                if (t.name != "Elevators map door")
                    continue;
                log.LogInfo($"[Probe] {name} map door mark local {t.localPosition} yaw {t.eulerAngles.y:0} on {(t.parent != null ? t.parent.name : "?")}");
            }
            found += ShaftScan(name);
            log.LogInfo($"[Probe] {name} opening scan: {found} finding(s)");
        }

        private static bool HasChild(Transform root, string childName)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == childName) return true;
            return false;
        }

        // Rays out of the shaft's middle at every metre of its height: each should meet a wall
        // within reach, except the door side across a dock's doorway.
        private int ShaftScan(string name)
        {
            GameObject shaft = GameObject.Find("Elevators shaft");
            if (shaft == null)
            {
                log.LogInfo($"[Probe] {name} SHAFT: none built");
                return 0;
            }
            float bottom = float.MaxValue, top = float.MinValue;
            foreach (Renderer r in shaft.GetComponentsInChildren<Renderer>())
            {
                bottom = Mathf.Min(bottom, r.bounds.min.y);
                top = Mathf.Max(top, r.bounds.max.y);
            }
            var dockYs = new List<float>();
            foreach ((Vector3 pos, float yaw, string dock) in Elevator.DockList())
                dockYs.Add(pos.y);
            int holes = 0;
            int rays = 0;
            Transform frame = shaft.transform;
            for (float y = bottom + 1f; y < top - 0.5f; y += 1f)
            {
                foreach (Vector3 local in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                {
                    Vector3 origin = new Vector3(frame.position.x, y, frame.position.z);
                    Vector3 dir = frame.TransformDirection(local);
                    rays++;
                    if (Physics.Raycast(origin, dir, 3.6f, ~0, QueryTriggerInteraction.Ignore))
                        continue;
                    bool doorway = false;
                    if (local == Vector3.back)
                        foreach (float dy in dockYs) if (y > dy - 0.3f && y < dy + 3.4f) doorway = true;
                    if (doorway)
                        continue;
                    holes++;
                    if (holes <= 12)
                        log.LogWarning($"[Probe] {name} SHAFT HOLE at y {y:0.0} toward {local} from {origin}");
                }
            }
            log.LogInfo($"[Probe] {name} SHAFT {(holes == 0 ? "ok" : "HOLES")}: {holes} of {rays} rays escaped, {bottom:0.0}..{top:0.0}");
            return holes;
        }

        // Open the map through its debug switch, step through the floors the way the wheel would,
        // and save what the map camera draws for each one, with the layer heights it settled on.
        private IEnumerator MapCheck(string name)
        {
            Map map = Map.Instance;
            LiftService? lift = LiftService.Instance;
            var buttons = Object.FindObjectsOfType<Elevator.LiftButton>();
            int withArea = 0;
            foreach (Elevator.LiftButton b in buttons)
                if (b.GetComponent<PhysGrabObjectGrabArea>() != null && b.GetComponent<StaticGrabObject>() != null && b.GetComponent<Photon.Pun.PhotonView>() != null) withArea++;
            log.LogInfo($"[Probe] {name} buttons={buttons.Length} attached={Count(buttons, b => b.Attached)} withGrabArea={withArea}");
            if (map == null || lift == null)
            {
                log.LogWarning($"[Probe] {name}: no map or lift, map check skipped");
                yield break;
            }
            Camera? cam = map.ActiveParent != null ? map.ActiveParent.GetComponentInChildren<Camera>(true) : null;
            map.debugActive = true;
            yield return new WaitForSecondsRealtime(1.5f);
            log.LogInfo($"[Probe] {name} map active={map.Active} floors={lift.FloorCount} playerLayer={map.PlayerLayer} cam={(cam != null ? cam.name : "none")} rt={(cam != null && cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "none")}");
            for (int f = 0; f < lift.FloorCount; f++)
            {
                Elevators.Patches.MapFloorsPatch.Viewing = f;
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                var layers = new System.Text.StringBuilder();
                foreach (MapLayer layer in map.Layers)
                    layers.Append($" L{layer.layer}@{layer.transform.localPosition.y:0.0}({layer.transform.childCount})");
                Transform? label = cam != null ? cam.transform.Find("Elevators floor") : null;
                string labelInfo = "none";
                if (label != null)
                {
                    var r = label.GetComponent<Renderer>();
                    var tmp = label.GetComponent<TMPro.TMP_Text>();
                    Vector3 vp = cam != null ? cam.WorldToViewportPoint(label.position) : Vector3.zero;
                    labelInfo = $"active={label.gameObject.activeInHierarchy} layer={label.gameObject.layer} renderer={(r != null ? r.enabled + "/" + r.isVisible + "/" + r.bounds.size : "none")} verts={(tmp != null && tmp.mesh != null ? tmp.mesh.vertexCount : -1)} chars={(tmp != null ? tmp.textInfo.characterCount : -1)} face={(tmp != null && tmp.fontMaterial != null && tmp.fontMaterial.HasProperty("_FaceColor") ? tmp.fontMaterial.GetColor("_FaceColor").ToString() : "?")} shader={(tmp != null && tmp.fontMaterial != null ? tmp.fontMaterial.shader.name : "?")} text='{(tmp != null ? tmp.text : "?")}' color={(tmp != null ? tmp.color.ToString() : "?")} viewport={vp} camRot={(cam != null ? cam.transform.rotation.eulerAngles.ToString() : "?")} camPos={(cam != null ? cam.transform.position.ToString() : "?")} local={label.localPosition} scale={label.localScale}";
                }
                var marks = new System.Text.StringBuilder();
                foreach (TMPro.TMP_Text tm in map.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (tm.text == "?" && tm.gameObject.activeInHierarchy && marks.Length < 400)
                        marks.Append($" ?@{tm.transform.position.y:0.00}/{(tm.GetComponentInParent<MapLayer>() != null ? tm.GetComponentInParent<MapLayer>().layer : 999)}");
                }
                log.LogInfo($"[Probe] {name} map floor {f} '{Elevators.Patches.MapFloorsPatch.ViewingName}' playerLayer={map.PlayerLayer} label {labelInfo} cull={(cam != null ? cam.cullingMask : 0)} over={(map.OverLayerParent != null ? map.OverLayerParent.name + "/" + map.OverLayerParent.childCount + "@" + map.OverLayerParent.localPosition.y.ToString("0.0") : "none")} marks:{marks} layers:{layers}");
                if (cam != null && cam.targetTexture != null)
                {
                    RenderTexture rt = cam.targetTexture;
                    RenderTexture was = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = was;
                    string file = Path.Combine(Paths.BepInExRootPath, $"probe-map-{name}-{f}.png");
                    File.WriteAllBytes(file, tex.EncodeToPNG());
                    Object.Destroy(tex);
                    log.LogInfo($"[Probe] {name} map image {file}");
                }
            }
            // Discover a room on the player's floor while the map is open on another floor: the
            // question mark must still clear.
            if (lift.FloorCount >= 2 && PlayerAvatar.instance != null)
            {
                int mine = lift.FloorIndexOfHeight(PlayerAvatar.instance.transform.position.y);
                Elevators.Patches.MapFloorsPatch.Viewing = mine == 0 ? 1 : 0;
                yield return new WaitForSecondsRealtime(0.3f);
                RoomVolume? target = null;
                foreach (RoomVolume rv in Object.FindObjectsOfType<RoomVolume>())
                {
                    if (rv.MapModule == null || rv.Module == null) continue;
                    if (lift.FloorIndexOfHeight(rv.transform.position.y) != mine) continue;
                    if (rv.Module.gameObject.name.StartsWith("Start")) continue;
                    if (rv.MapModule.transform.localScale.x < 0.99f) continue;
                    target = rv; break;
                }
                if (target != null)
                {
                    MapModule mm = target.MapModule;
                    Collider? vol = target.GetComponent<Collider>();
                    Vector3 spot = (vol != null ? vol.bounds.center : target.transform.position) + Vector3.up * 0.3f;
                    if (NavMesh.SamplePosition(spot, out NavMeshHit onFloor, 3f, NavMesh.AllAreas)) spot = onFloor.position + Vector3.up * 0.3f;
                    Place(PlayerAvatar.instance, spot);
                    yield return new WaitForSecondsRealtime(2.5f);
                    map.debugActive = false;
                    yield return new WaitForSecondsRealtime(2.5f);
                    bool cleared = mm == null || mm.transform.localScale.x < 0.99f;
                    log.LogInfo($"[Probe] {name} DISCOVER {(cleared ? "ok" : "FAIL")} room {target.Module.name} map object {(mm == null ? "gone" : "scale " + mm.transform.localScale.x.ToString("0.00"))}");
                }
                else
                    log.LogInfo($"[Probe] {name} DISCOVER n/a, no unexplored room on this floor");
            }
            map.debugActive = false;
            yield return new WaitForSecondsRealtime(1f);
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
            for (int attempt = 0; attempt < 3 && !Elevator.IsInside(me.transform.position); attempt++)
            {
                Place(me, ground + Vector3.up * 0.6f);
                yield return new WaitForSecondsRealtime(1.5f);
                if (!Elevator.IsInside(me.transform.position))
                    log.LogInfo($"[Probe] {name} PLACE try {attempt + 1}: landed at {me.transform.position}, tumbling={me.isTumbling}, wanted {ground + Vector3.up * 0.6f}");
            }
            int upDock = DockNamed("upstairs");
            // What the grabber does: the hit's transform must carry the StaticGrabObject.
            foreach (Elevator.LiftButton b in Object.FindObjectsOfType<Elevator.LiftButton>())
            {
                Vector3 axis = b.transform.up;
                bool hit = Physics.Raycast(b.transform.position + axis * 0.8f, -axis, out RaycastHit h, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                log.LogInfo($"[Probe] {name} ray at {b.Kind}/{b.Dock}: hit={(hit ? h.transform.name : "nothing")} static={(hit && h.transform.GetComponent<StaticGrabObject>() != null)} rb={(hit && h.rigidbody != null ? h.rigidbody.name : "none")}");
            }
            // The call button at the dock the car stands on opens the doors.
            Elevator.LiftButton? callHere = null;
            foreach (Elevator.LiftButton b in Object.FindObjectsOfType<Elevator.LiftButton>())
                if (b.Kind == Elevator.ButtonKind.Call && b.Dock == Elevator.CurrentDock && b.Attached) { callHere = b; break; }
            if (callHere != null && me.physGrabber != null)
            {
                log.LogInfo($"[Probe] {name} before call: {Elevator.DoorState()}");
                StaticGrabObject cg = callHere.GetComponent<StaticGrabObject>();
                cg.GrabStarted(me.physGrabber);
                yield return new WaitForSecondsRealtime(0.4f);
                cg.GrabEnded(me.physGrabber);
                yield return new WaitForSecondsRealtime(1.5f);
                log.LogInfo($"[Probe] {name} CALL OPENS {(Elevator.DoorOpenAmount > 0.5f ? "ok" : "FAIL")}: {Elevator.DoorState()}");
                // With the doors open, is the way from the landing into the car clear?
                // From just outside the doors, straight in.
                (Vector3 gpos, float gyaw, string _) = Elevator.DockList().Find(d => d.name == "ground");
                Vector3 landingSpot = gpos + Quaternion.Euler(0f, gyaw, 0f) * new Vector3(0f, 0f, -3f);
                Vector3 carCentre = ground + Vector3.up * 0.9f;
                foreach (float h in new[] { 0.4f, 1.0f, 1.6f })
                {
                    Vector3 from = new Vector3(landingSpot.x, ground.y + h, landingSpot.z);
                    Vector3 dir = (new Vector3(carCentre.x, from.y, carCentre.z) - from);
                    float dist = dir.magnitude;
                    bool hit = Physics.Raycast(from, dir.normalized, out RaycastHit h2, dist, ~0, QueryTriggerInteraction.Ignore);
                    log.LogInfo($"[Probe] {name} WAY IN at h {h}: {(hit ? $"blocked by {h2.collider.name} (layer {h2.collider.gameObject.layer}, {h2.distance:0.0} m of {dist:0.0})" : "clear")}");
                }
                yield return new WaitForSecondsRealtime(5f);
                log.LogInfo($"[Probe] {name} after hold: {Elevator.DoorState()}");
            }
            yield return new WaitForSecondsRealtime(3f);
            NavCheck(name, "car docked here", ground, expectPath: true);
            Elevator.LiftButton? upButton = Elevator.FindButton(Elevator.ButtonKind.Up);
            log.LogInfo($"[Probe] {name} pressing up (dock {upDock}) by grabbing={(upButton != null)}, inside={Elevator.IsInside(me.transform.position)} at={me.transform.position} dock={ground}");
            if (upButton != null && me.physGrabber != null)
            {
                StaticGrabObject grab = upButton.GetComponent<StaticGrabObject>();
                grab.GrabStarted(me.physGrabber);
                yield return new WaitForSecondsRealtime(0.5f);
                grab.GrabEnded(me.physGrabber);
                yield return new WaitForSecondsRealtime(0.5f);
                if (!Elevator.Moving)
                {
                    log.LogWarning($"[Probe] {name} the grabbed up button started no ride");
                    ElevatorsNet.RequestRide(upDock);
                }
            }
            else
                ElevatorsNet.RequestRide(upDock);
            yield return new WaitForSecondsRealtime(1.5f);
            log.LogInfo($"[Probe] {name} hum during ride: {Elevator.HumStatus()}");
            yield return RideTelemetry(name, me, "UP");
            log.LogInfo($"[Probe] {name} hum after ride: {Elevator.HumStatus()}");
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR UP", me.transform.position, up);
            NavCheck(name, "car gone from here", ground, expectPath: false);
            NavCheck(name, "car docked upstairs", up, expectPath: true);
            // Something loose rides down with us: a small valuable dropped by the panel.
            ValuableObject? cargo = null;
            foreach (ValuableObject v in Object.FindObjectsOfType<ValuableObject>())
            {
                if (v.physGrabObject != null && v.volumeType <= ValuableVolume.Type.Small) { cargo = v; break; }
            }
            if (cargo != null)
            {
                cargo.physGrabObject.Teleport(me.transform.position + Vector3.right * 0.8f + Vector3.up * 0.5f, Quaternion.identity);
                yield return new WaitForSecondsRealtime(1f);
            }
            ElevatorsNet.RequestRide(DockNamed("ground"));
            yield return new WaitForSecondsRealtime(1.5f);
            yield return RideTelemetry(name, me, "DOWN", cargo);
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR DOWN", me.transform.position, ground);
            // Ride again while tumbling: a knocked-down rider must go with the car too.
            if (me.tumble != null)
            {
                Place(me, ground + Vector3.up * 0.6f);
                yield return new WaitForSecondsRealtime(1f);
                me.tumble.TumbleSet(true, false);
                yield return new WaitForSecondsRealtime(1.5f);
                log.LogInfo($"[Probe] {name} tumbling in the car: {me.isTumbling}, inside={Elevator.IsInside(me.transform.position)}");
                ElevatorsNet.RequestRide(upDock);
                float tw = 0f;
                while (Elevator.Moving && tw < 30f) { yield return new WaitForSecondsRealtime(0.5f); tw += 0.5f; }
                yield return new WaitForSecondsRealtime(1f);
                Verdict(name, "TUMBLE RIDE UP", me.transform.position, up);
                me.tumble.TumbleSet(false, false);
                yield return new WaitForSecondsRealtime(1.5f);
                ElevatorsNet.RequestRide(DockNamed("ground"));
                tw = 0f;
                while (Elevator.Moving && tw < 30f) { yield return new WaitForSecondsRealtime(0.5f); tw += 0.5f; }
                yield return new WaitForSecondsRealtime(1f);
                Verdict(name, "TUMBLE RIDE BACK", me.transform.position, ground);
            }
            yield return MonsterTests(name, me, ground, up, upDock);
            if (Session.Up != null)
            {
                var from = new Vector3(ground.x + 6f, Session.GroundTop + 2f, ground.z);
                float roofY = Physics.Raycast(from, Vector3.down, out RaycastHit roof, 200f) ? roof.point.y : ground.y;
                Place(me, new Vector3(from.x, roofY + 4f, from.z));
                yield return new WaitForSecondsRealtime(6f);
                bool inRoom = Physics.CheckSphere(me.transform.position + Vector3.up * 0.6f, 0.25f, LayerMask.GetMask("RoomVolume"), QueryTriggerInteraction.Collide);
                bool onNav = UnityEngine.AI.NavMesh.SamplePosition(me.transform.position, out UnityEngine.AI.NavMeshHit navHit, 1.2f, UnityEngine.AI.NavMesh.AllAreas) && Mathf.Abs(navHit.position.y - me.transform.position.y) < 1.2f;
                if (inRoom || onNav)
                    log.LogInfo($"[Probe] {name} VOID RESCUE n/a, the drop landed on level floor at {me.transform.position}");
                else
                    Verdict(name, "VOID RESCUE", me.transform.position, ground);
            }
        }

        // How far the rider and any cargo stray from the car floor while it moves.
        // A real monster: it walks into the docked car after us, rides up with us and steps out
        // at the landing, and on its own it presses the call button and rides between floors.
        private IEnumerator MonsterTests(string name, PlayerAvatar me, Vector3 ground, Vector3 up, int upDock)
        {
            // A walker that investigates, the quick ones first; the slow ones would time the walk out.
            EnemyParent? monster = null;
            string[] quick = { "Rugrat", "Gnome", "Robe", "Huntsman", "Reaper", "Upscream", "Animal", "Spewer", "Head" };
            if (EnemyDirector.instance != null)
            {
                int bestRank = int.MaxValue;
                foreach (EnemyParent p in EnemyDirector.instance.enemiesSpawned)
                {
                    if (p == null || !p.Spawned || p.Enemy == null || !p.Enemy.HasStateInvestigate || p.Enemy.GetComponentInChildren<EnemyThinMan>(true) != null)
                        continue;
                    int rank = System.Array.FindIndex(quick, q => p.enemyName != null && p.enemyName.Contains(q));
                    if (rank < 0) rank = quick.Length;
                    if (rank < bestRank) { bestRank = rank; monster = p; }
                }
            }
            int groundDock = DockNamed("ground");
            Vector3 groundLanding = Elevator.LandingOf("ground");
            Vector3 upLanding = Elevator.LandingOf("upstairs");
            // A known chaser of our own, spawned the way the level spawns them, beats whatever rolled.
            EnemyParent? summoned = null;
            yield return Summon(name, groundLanding, p => summoned = p);
            if (summoned != null)
                monster = summoned;
            if (monster == null)
            {
                log.LogWarning($"[Probe] {name} MONSTER: nothing spawned to test with");
                yield break;
            }
            float patrolInterval = PluginConfig.MonsterPatrolInterval.Value;
            yield return HoldPatrols();
            if (Elevator.CurrentDock != groundDock)
            {
                ElevatorsNet.RequestRide(groundDock);
                float w = 0f;
                yield return new WaitForSecondsRealtime(1.5f);
                while (Elevator.Moving && w < 40f) { yield return new WaitForSecondsRealtime(0.5f); w += 0.5f; }
                yield return new WaitForSecondsRealtime(2f);
            }
            Place(me, ground + Vector3.up * 0.6f);
            yield return new WaitForSecondsRealtime(1f);
            yield return PutMonster(name, monster, groundLanding, "WALK IN setup");
            log.LogInfo($"[Probe] {name} MONSTER {monster.enemyName} spawned={monster.Spawned} state={monster.Enemy.CurrentState} at {monster.Enemy.transform.position}, landing {groundLanding}");
            // Standing at the shut doors it presses the call button on its own.
            float knock = 0f;
            while (knock < 8f && Elevator.DoorOpenAmount < 0.5f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                knock += 0.5f;
                if (monster.Enemy != null && Vector3.Distance(monster.Enemy.transform.position, groundLanding) > 3f)
                    monster.Enemy.EnemyTeleported(groundLanding);
            }
            log.LogInfo($"[Probe] {name} MONSTER OPENS DOORS {(Elevator.DoorOpenAmount >= 0.5f ? "ok" : "FAIL")}: {Elevator.DoorState()} after {knock:0}s");
            ElevatorsNet.SendDoors(14f);
            yield return new WaitForSecondsRealtime(1.5f);
            var path = new NavMeshPath();
            bool canPath = NavMesh.CalculatePath(monster.Enemy.transform.position, ground, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            log.LogInfo($"[Probe] {name} MONSTER PATH {monster.enemyName} landing->car: {(canPath ? "ok" : "FAIL")} status={path.status} from={monster.Enemy.transform.position}");
            // It sees us standing in the open car and comes for us, the way it happens in play.
            monster.Enemy.SetChaseTarget(me);
            monster.Enemy.StateInvestigate.Set(ground, false);
            float best = 99f, t = 0f;
            bool inside = false;
            while (t < 25f && !inside)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                t += 0.5f;
                if (monster == null || !monster.Spawned || monster.Enemy == null)
                    break;
                Vector3 at = monster.Enemy.transform.position;
                best = Mathf.Min(best, Vector3.Distance(at, ground));
                inside = Elevator.IsInside(at);
                if (t % 4f < 0.5f)
                {
                    monster.Enemy.SetChaseTarget(me);
                    monster.Enemy.StateInvestigate.Set(ground, false);
                    ElevatorsNet.SendDoors(14f);
                }
            }
            log.LogInfo($"[Probe] {name} WALK IN chase: {monster?.enemyName} state={monster?.Enemy?.CurrentState} chase={monster?.Enemy?.CheckChase()}");
            log.LogInfo($"[Probe] {name} MONSTER WALKS IN {(inside ? "ok" : "FAIL")}: {monster?.enemyName} closest {best:0.0} m after {t:0}s at {monster?.Enemy?.transform.position}");
            ElevatorsNet.SendDoors(0f);
            if (inside && monster != null)
            {
                yield return new WaitForSecondsRealtime(2.5f);
                ElevatorsNet.RequestRide(upDock);
                float w = 0f;
                yield return new WaitForSecondsRealtime(1.5f);
                while (Elevator.Moving && w < 30f) { yield return new WaitForSecondsRealtime(0.5f); w += 0.5f; }
                yield return new WaitForSecondsRealtime(2f);
                Vector3 at = monster.Enemy != null ? monster.Enemy.transform.position : Vector3.zero;
                float d = Vector3.Distance(at, upLanding);
                log.LogInfo($"[Probe] {name} MONSTER RIDES {(d < 5f ? "ok" : "FAIL")}: {monster.enemyName} at {at}, upstairs landing {upLanding}, {d:0.0} m off");
            }
            if (monster == null || monster.Enemy == null)
                yield break;
            // Patrol: the monster upstairs on its own, us far away on the ground floor.
            Vector3 far = ground;
            float farthest = 0f;
            foreach (LevelPoint point in Object.FindObjectsOfType<LevelPoint>())
            {
                Vector3 pp = point.transform.position;
                if (Mathf.Abs(pp.y - ground.y) > 2f)
                    continue;
                float d = Vector3.Distance(pp, ground);
                if (d > farthest) { farthest = d; far = pp; }
            }
            Place(me, far + Vector3.up * 0.3f);
            if (Elevator.CurrentDock != upDock)
            {
                ElevatorsNet.RequestRide(upDock);
                float w = 0f;
                yield return new WaitForSecondsRealtime(1.5f);
                while (Elevator.Moving && w < 30f) { yield return new WaitForSecondsRealtime(0.5f); w += 0.5f; }
                yield return new WaitForSecondsRealtime(2f);
            }
            yield return HoldPatrols();
            yield return PutMonster(name, monster, upLanding, "PATROL setup");
            log.LogInfo($"[Probe] {name} PATROL start: {monster.enemyName} at {monster.Enemy.transform.position} (upstairs landing {upLanding}), car at dock {Elevator.CurrentDock}");
            Elevator.LastPatrolRider = null;
            Elevator.StartPatrol(monster, upDock, groundDock);
            float pt = 0f;
            bool done = false;
            while (pt < 70f && !done)
            {
                yield return new WaitForSecondsRealtime(1f);
                pt += 1f;
                if (monster == null || monster.Enemy == null)
                    break;
                done = Elevator.LastPatrolRider == monster && Elevator.CurrentDock == groundDock && !Elevator.Moving && Vector3.Distance(monster.Enemy.transform.position, groundLanding) < 5f;
            }
            log.LogInfo($"[Probe] {name} MONSTER PATROL {(done ? "ok" : "FAIL")}: after {pt:0}s car at dock {Elevator.CurrentDock} moving={Elevator.Moving}, {monster?.enemyName} at {monster?.Enemy?.transform.position}, ground landing {groundLanding}");
            yield return new WaitForSecondsRealtime(1f);
            yield return ComesForYou(name, me, ground, up, groundDock, upDock, monster);
            PluginConfig.MonsterPatrolInterval.Value = patrolInterval;
        }

        // End to end, nothing staged: we stand upstairs far from the shaft, a monster is downstairs,
        // and the mod's own patrols have to bring one up and let it find us. Then the chase: one
        // that saw us as the doors shut has to turn up on our floor and reach us.
        private IEnumerator ComesForYou(string name, PlayerAvatar me, Vector3 ground, Vector3 up, int groundDock, int upDock, EnemyParent monster)
        {
            yield return HoldPatrols();
            Vector3 hide = FarReachable(Elevator.LandingOf("upstairs"), up.y, 10f);
            Place(me, hide + Vector3.up * 0.3f);
            if (Elevator.CurrentDock != groundDock)
            {
                ElevatorsNet.RequestRide(groundDock);
                float w = 0f;
                yield return new WaitForSecondsRealtime(1.5f);
                while (Elevator.Moving && w < 30f) { yield return new WaitForSecondsRealtime(0.5f); w += 0.5f; }
                yield return new WaitForSecondsRealtime(2f);
            }
            var already = new HashSet<EnemyParent>();
            foreach (EnemyParent p in EnemyDirector.instance.enemiesSpawned)
                if (p != null && p.Spawned && p.Enemy != null && Mathf.Abs(p.Enemy.transform.position.y - up.y) < 4f) already.Add(p);
            // Far from the dock, the way a roamer really is; near it only when the floor is small.
            Vector3 start = FarPoint(ground, ground.y, 30f, 90f);
            if (start == ground)
                start = FarPoint(ground, ground.y, 8f, 20f);
            yield return PutMonster(name, monster, start, "COMES UP setup");
            PluginConfig.MonsterPatrolInterval.Value = 12f;
            log.LogInfo($"[Probe] {name} COMES UP: we hide at {hide}, {monster.enemyName} set down at {start}, {already.Count} monster(s) already upstairs, patrol every {PluginConfig.MonsterPatrolInterval.Value / Mathf.Max(1, Elevator.DockList().Count - 1):0}s");
            EnemyParent? comer = null;
            float t = 0f;
            while (t < 180f && comer == null)
            {
                yield return new WaitForSecondsRealtime(1f);
                t += 1f;
                // Only a monster the car itself delivered counts: the game respawns monsters
                // wherever it likes, and one popping up on our floor by respawn proves nothing.
                foreach (EnemyParent p in EnemyDirector.instance.enemiesSpawned)
                {
                    if (p == null || !p.Spawned || p.Enemy == null || already.Contains(p) || !Elevator.Arrivals.Contains(p))
                        continue;
                    if (Mathf.Abs(p.Enemy.transform.position.y - up.y) < 4f) { comer = p; break; }
                }
            }
            log.LogInfo($"[Probe] {name} MONSTER COMES UP {(comer != null ? "ok" : "FAIL")}: {(comer != null ? $"{comer.enemyName} rode up to our floor on its own after {t:0}s, now at {comer.Enemy.transform.position}" : $"nobody rode up in {t:0}s")}");
            if (comer != null)
                yield return ReachesYou(name, me, comer, "after the patrol");
            yield return HoldPatrols();

            // The chase. In the car at the ground with the doors open, it sees us, we ride.
            if (monster == null || monster.Enemy == null || monster.Enemy.Vision == null || me.photonView == null)
                yield break;
            Place(me, ground + Vector3.up * 0.6f);
            if (Elevator.CurrentDock != groundDock)
            {
                ElevatorsNet.RequestRide(groundDock);
                float w = 0f;
                yield return new WaitForSecondsRealtime(1.5f);
                while (Elevator.Moving && w < 30f) { yield return new WaitForSecondsRealtime(0.5f); w += 0.5f; }
                yield return new WaitForSecondsRealtime(2f);
            }
            ElevatorsNet.SendDoors(6f);
            yield return PutMonster(name, monster, Elevator.LandingOf("ground"), "CHASE setup");
            bool downstairs = monster.Enemy != null && Mathf.Abs(monster.Enemy.transform.position.y - ground.y) < 4f;
            monster.Enemy.SetChaseTarget(me);
            yield return new WaitForSecondsRealtime(2f);
            log.LogInfo($"[Probe] {name} CHASE: {monster.enemyName} state={monster.Enemy.CurrentState} chase={monster.Enemy.CheckChase()} at {monster.Enemy.transform.position}");
            ElevatorsNet.SendDoors(0f);
            yield return new WaitForSecondsRealtime(2.5f);
            // Every kind chases in its own way; what counts here is that it is chasing as the car
            // leaves, which the game marks with its chase timer, held on frame by frame.
            // We have to be in the car when it leaves, or nothing was chasing a rider.
            if (!Elevator.IsInside(me.transform.position))
            {
                Place(me, ground + Vector3.up * 0.6f);
                yield return new WaitForSecondsRealtime(1f);
            }
            log.LogInfo($"[Probe] {name} CHASE depart: us inside={Elevator.IsInside(me.transform.position)} at {me.transform.position}, {monster.enemyName} {Vector3.Distance(monster.Enemy.transform.position, ground):0.0} m from the car, doors {Elevator.DoorState()}");
            float r = 0f;
            bool sent = false;
            for (float hold = 0f; hold < 2.5f; hold += Time.unscaledDeltaTime)
            {
                if (monster.Enemy != null) monster.Enemy.SetChaseTimer();
                if (!sent && hold > 0.5f) { ElevatorsNet.RequestRide(upDock); sent = true; }
                yield return null;
            }
            while (Elevator.Moving && r < 30f) { yield return new WaitForSecondsRealtime(0.5f); r += 0.5f; }
            Place(me, hide + Vector3.up * 0.3f);
            float f = 0f;
            bool followed = false;
            while (f < 30f && !followed)
            {
                yield return new WaitForSecondsRealtime(1f);
                f += 1f;
                followed = monster.Enemy != null && Mathf.Abs(monster.Enemy.transform.position.y - up.y) < 4f;
            }
            log.LogInfo($"[Probe] {name} MONSTER FOLLOWS {(!downstairs ? "n/a (it was not downstairs at the start)" : followed ? "ok" : "FAIL")}: {monster.enemyName} {(followed ? $"on our floor {f:0}s after the doors shut" : $"still at {monster.Enemy?.transform.position} after {f:0}s")}");
            if (followed)
                yield return ReachesYou(name, me, monster, "after the chase");
        }

        // Spawn one of the game's chasers through the level's own spawn call and wait for it to
        // stand up; the director puts a fresh one at a first spawn point, so it is fetched after.
        private IEnumerator Summon(string name, Vector3 at, System.Action<EnemyParent?> got)
        {
            var want = new List<string> { "Rugrat", "Robe", "Hunter", "Reaper", "Upscream", "Gnome", "Animal" };
            if (wantMonster != null)
                want.Insert(0, wantMonster);
            EnemySetup? pick = null;
            int bestRank = int.MaxValue;
            EnemyDirector director = EnemyDirector.instance;
            if (director == null || LevelGenerator.Instance == null) { got(null); yield break; }
            foreach (List<EnemySetup> list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            {
                if (list == null) continue;
                foreach (EnemySetup setup in list)
                {
                    if (setup == null || setup.spawnObjects == null || setup.spawnObjects.Count == 0) continue;
                    string n = setup.spawnObjects[0].Prefab != null ? setup.spawnObjects[0].Prefab.name : setup.spawnObjects[0].ResourcePath ?? "";
                    int rank = want.FindIndex(w => n.Contains(w));
                    if (rank >= 0 && rank < bestRank) { bestRank = rank; pick = setup; }
                }
            }
            if (pick == null)
            {
                log.LogInfo($"[Probe] {name} SUMMON: no known chaser in the director's lists");
                got(null);
                yield break;
            }
            var before = new HashSet<EnemyParent>(Object.FindObjectsOfType<EnemyParent>());
            LevelGenerator.Instance.EnemySpawn(pick, at);
            EnemyParent? fresh = null;
            float t = 0f;
            while (t < 20f && fresh == null)
            {
                yield return new WaitForSecondsRealtime(1f);
                t += 1f;
                foreach (EnemyParent p in Object.FindObjectsOfType<EnemyParent>())
                    if (!before.Contains(p) && p.Spawned && p.Enemy != null) { fresh = p; break; }
            }
            if (fresh != null && !director.enemiesSpawned.Contains(fresh))
                director.enemiesSpawned.Add(fresh);
            // A fresh one can drop straight back into its despawn; it is kept up until it stays up.
            float up = 0f;
            while (fresh != null && up < 20f && (!fresh.Spawned || fresh.Enemy == null || fresh.Enemy.CurrentState == EnemyState.Despawn || fresh.Enemy.CurrentState == EnemyState.Spawn))
            {
                fresh.SpawnedTimerSet(600f);
                fresh.SpawnedTimerPause(600f);
                yield return new WaitForSecondsRealtime(1f);
                up += 1f;
            }
            if (fresh != null) { fresh.SpawnedTimerSet(600f); fresh.SpawnedTimerPause(600f); }
            log.LogInfo($"[Probe] {name} SUMMON {(fresh != null ? "ok" : "FAIL")}: {pick.name} -> {fresh?.enemyName} after {t:0}s, up after {up:0}s more, spawned={fresh?.Spawned} state={fresh?.Enemy?.CurrentState} at {fresh?.Enemy?.transform.position}");
            got(fresh);
        }

        // A teleport that took: the mod's own patrols can move a monster under us, so each one is
        // checked and repeated, and the monster is kept spawned for the whole test.
        private IEnumerator PutMonster(string name, EnemyParent monster, Vector3 at, string what)
        {
            monster.SpawnedTimerSet(600f);
            monster.SpawnedTimerPause(600f);
            for (int i = 0; i < 3; i++)
            {
                float up = 0f;
                while (up < 15f && (!monster.Spawned || monster.Enemy == null || monster.Enemy.CurrentState == EnemyState.Despawn || monster.Enemy.CurrentState == EnemyState.Spawn))
                {
                    monster.SpawnedTimerSet(600f);
                    yield return new WaitForSecondsRealtime(1f);
                    up += 1f;
                }
                if (monster.Enemy == null) break;
                monster.Enemy.EnemyTeleported(at);
                yield return new WaitForSecondsRealtime(1.5f);
                if (monster.Enemy == null || Vector3.Distance(monster.Enemy.transform.position, at) < 3f)
                    break;
                log.LogInfo($"[Probe] {name} {what}: {monster.enemyName} did not stay at {at}, it is at {monster.Enemy.transform.position} (spawned={monster.Spawned}, state={monster.Enemy.CurrentState}), again");
            }
        }

        // Nothing of the mod's own scheduling while a step is staged.
        private IEnumerator HoldPatrols()
        {
            PluginConfig.MonsterPatrolInterval.Value = 0f;
            float w = 0f;
            while (Elevator.Patrolling && w < 60f) { yield return new WaitForSecondsRealtime(1f); w += 1f; }
        }

        // From wherever it stands on our floor, the game's own investigate has to bring it to us.
        private IEnumerator ReachesYou(string name, PlayerAvatar me, EnemyParent comer, string when)
        {
            // Our floor has to be one navmesh from where it stands to where we are; the walk
            // itself is the game's own AI and it takes its time, so the path is the verdict and
            // the approach is reported alongside.
            var path = new NavMeshPath();
            bool from = NavMesh.SamplePosition(comer.Enemy.transform.position, out NavMeshHit a, 2f, NavMesh.AllAreas);
            bool to = NavMesh.SamplePosition(me.transform.position, out NavMeshHit b, 2f, NavMesh.AllAreas);
            bool complete = from && to && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            log.LogInfo($"[Probe] {name} MONSTER CAN REACH YOU {(complete ? "ok" : "FAIL")} {when}: {comer.enemyName} at {comer.Enemy.transform.position}, us at {me.transform.position}, path={path.status} corners={path.corners.Length}");
            float t = 0f;
            float best = 999f;
            bool near = false;
            while (t < 45f && !near)
            {
                if (t % 5f < 0.5f)
                    EnemyDirector.instance.SetInvestigate(me.transform.position, 300f);
                yield return new WaitForSecondsRealtime(1f);
                t += 1f;
                if (comer == null || comer.Enemy == null)
                    break;
                float d = Vector3.Distance(comer.Enemy.transform.position, me.transform.position);
                best = Mathf.Min(best, d);
                near = d < 6f;
            }
            log.LogInfo($"[Probe] {name} MONSTER APPROACH {when}: {comer?.enemyName} closest {best:0.0} m in {t:0}s{(near ? ", reached us" : ", the game's AI did not come over in that time")}");
        }

        // The farthest level point on the floor that the given spot can actually path to.
        private static Vector3 FarReachable(Vector3 from, float floorY, float min)
        {
            Vector3 best = from;
            float bestD = -1f;
            var path = new NavMeshPath();
            if (!NavMesh.SamplePosition(from, out NavMeshHit a, 2f, NavMesh.AllAreas))
                return FarPoint(from, floorY, min);
            foreach (LevelPoint point in Object.FindObjectsOfType<LevelPoint>())
            {
                Vector3 pp = point.transform.position;
                if (Mathf.Abs(pp.y - floorY) > 2f)
                    continue;
                float d = Vector2.Distance(new Vector2(pp.x, pp.z), new Vector2(from.x, from.z));
                if (d < min || d <= bestD)
                    continue;
                if (!NavMesh.SamplePosition(pp, out NavMeshHit b, 2f, NavMesh.AllAreas))
                    continue;
                if (NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                {
                    bestD = d;
                    best = pp;
                }
            }
            return best;
        }

        // A level point on the given floor, at least min metres (and at most max) from a spot.
        private static Vector3 FarPoint(Vector3 from, float floorY, float min, float max = 9999f)
        {
            Vector3 best = from;
            float bestD = -1f;
            foreach (LevelPoint point in Object.FindObjectsOfType<LevelPoint>())
            {
                Vector3 pp = point.transform.position;
                if (Mathf.Abs(pp.y - floorY) > 2f)
                    continue;
                float d = Vector2.Distance(new Vector2(pp.x, pp.z), new Vector2(from.x, from.z));
                if (d < min || d > max)
                    continue;
                if (max < 9000f ? d < bestD || bestD < 0f : d > bestD) { bestD = d; best = pp; }
            }
            return best;
        }

        private IEnumerator RideTelemetry(string name, PlayerAvatar me, string what, ValuableObject? cargo = null)
        {
            if (me == null || PlayerController.instance == null)
            {
                log.LogWarning($"[Probe] {name} RIDE {what}: the player is gone, the level must have changed");
                yield break;
            }
            float riderMin = 99f, riderMax = -99f, cargoMin = 99f, cargoMax = -99f, prev = float.NaN, jitter = 0f, camMin = 99f, camMax = -99f;
            int samples = 0;
            float waited = 0f;
            string bumpAt = "";
            while (Elevator.Moving && waited < 30f)
            {
                yield return new WaitForFixedUpdate();
                waited += Time.fixedDeltaTime;
                if (Elevator.CarVelocityY == 0f) continue;
                float rider = PlayerController.instance.rb.position.y - Elevator.FloorY;
                if (rider > 0.3f && bumpAt.Length == 0)
                    bumpAt = $" bump at carY={Elevator.FloorY:0.0} rider={PlayerController.instance.rb.position}";
                riderMin = Mathf.Min(riderMin, rider); riderMax = Mathf.Max(riderMax, rider);
                if (!float.IsNaN(prev)) jitter = Mathf.Max(jitter, Mathf.Abs(rider - prev));
                if (Camera.main != null)
                {
                    float camRel = Camera.main.transform.position.y - PlayerController.instance.rb.position.y;
                    camMin = Mathf.Min(camMin, camRel); camMax = Mathf.Max(camMax, camRel);
                }
                prev = rider;
                if (cargo != null)
                {
                    float c = cargo.transform.position.y - Elevator.FloorY;
                    cargoMin = Mathf.Min(cargoMin, c); cargoMax = Mathf.Max(cargoMax, c);
                }
                samples++;
            }
            log.LogInfo($"[Probe] {name} RIDE {what} interp player={PlayerController.instance.rb.interpolation} samples={samples} rider above floor min={riderMin:0.00} max={riderMax:0.00} stepJitter={jitter:0.000} camAboveBody={camMin:0.00}..{camMax:0.00}{bumpAt}{(cargo != null ? $" cargo min={cargoMin:0.00} max={cargoMax:0.00} inside={Elevator.IsInside(cargo.transform.position)}" : "")}");
        }

        // Can a monster path from the landing into the car floor at this dock?
        private void NavCheck(string name, string what, Vector3 dock, bool expectPath)
        {
            Vector3 landing = Elevator.LandingOf(DockNamed(dock) ?? "");
            if (landing == Vector3.zero) landing = dock + Vector3.forward * -2.5f;
            bool onMesh = NavMesh.SamplePosition(dock, out NavMeshHit hit, 0.6f, NavMesh.AllAreas);
            var path = new NavMeshPath();
            bool ok = NavMesh.SamplePosition(landing, out NavMeshHit from, 1f, NavMesh.AllAreas) && onMesh && NavMesh.CalculatePath(from.position, hit.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            log.LogInfo($"[Probe] {name} NAV {what}: floorOnMesh={onMesh} pathComplete={ok} {(ok == expectPath ? "ok" : "FAIL")}");
            if (ok || !expectPath) return;
            var trace = new System.Text.StringBuilder();
            for (float t = 0f; t <= 1.001f; t += 0.1f)
            {
                Vector3 at = Vector3.Lerp(landing, dock, t);
                bool on = NavMesh.SamplePosition(at, out NavMeshHit step, 0.35f, NavMesh.AllAreas);
                trace.Append(on ? '#' : '.');
            }
            log.LogInfo($"[Probe] {name} NAV trace landing->car {trace} status={path.status} corners={path.corners.Length} landing={landing} car={dock}");
            // Straight out of the car through the doors: where the mesh stops and what stands there.
            (Vector3 dpos, float dyaw, string _) = Elevator.DockList().Find(d => d.name == DockNamed(dock));
            Quaternion facing = Quaternion.Euler(0f, dyaw, 0f);
            var axis = new System.Text.StringBuilder();
            var seen = new HashSet<string>();
            for (float z = 0f; z <= 4.5f; z += 0.3f)
            {
                Vector3 at = dpos + facing * new Vector3(0f, 0f, -z);
                bool on = NavMesh.SamplePosition(at, out NavMeshHit step, 0.35f, NavMesh.AllAreas);
                axis.Append(on ? '#' : '.');
                if (on) continue;
                foreach (Collider c in Physics.OverlapSphere(at + Vector3.up * 0.5f, 0.45f, ~0, QueryTriggerInteraction.Ignore))
                    seen.Add($"{c.name}/{LayerMask.LayerToName(c.gameObject.layer)}@{z:0.0}");
            }
            var obstacles = new List<string>();
            foreach (NavMeshObstacle o in Object.FindObjectsOfType<NavMeshObstacle>(true))
            {
                if (Vector3.Distance(o.transform.position, dpos) < 8f)
                    obstacles.Add($"{o.name}(enabled={o.enabled},carve={o.carving},size={o.size},{Vector3.Distance(o.transform.position, dpos):0.0}m)");
            }
            log.LogInfo($"[Probe] {name} NAV obstacles within 8 m of the dock: {(obstacles.Count > 0 ? string.Join("; ", obstacles) : "none")}");
            // The floor under that line: a sill or a step at the doorway breaks the mesh as surely as a wall.
            var profile = new System.Text.StringBuilder();
            for (float z = 0f; z <= 3.0f; z += 0.3f)
            {
                Vector3 at = dpos + facing * new Vector3(0f, 1.5f, -z);
                if (Physics.Raycast(at, Vector3.down, out RaycastHit fh, 3f, ~0, QueryTriggerInteraction.Ignore))
                    profile.Append($" {z:0.0}:{fh.point.y - dpos.y:+0.00;-0.00}({fh.collider.name})");
                else
                    profile.Append($" {z:0.0}:none");
            }
            log.LogInfo($"[Probe] {name} NAV floor profile out through the doors (meters above the dock):{profile}");
            var grid = new System.Text.StringBuilder();
            for (float z = 0f; z <= 3.0f; z += 0.3f)
            {
                grid.Append($" z{z:0.0} ");
                for (float x = -2.1f; x <= 2.1f; x += 0.3f)
                {
                    Vector3 at = dpos + facing * new Vector3(x, 0f, -z);
                    grid.Append(NavMesh.SamplePosition(at, out _, 0.35f, NavMesh.AllAreas) ? '#' : '.');
                }
                grid.Append(" |");
            }
            log.LogInfo($"[Probe] {name} NAV grid (x -2.1..2.1 across, rows out of the car):{grid}");
            log.LogInfo($"[Probe] {name} NAV axis car->room {axis} (0.3 m steps from the car center out through the doors); in the gaps: {(seen.Count > 0 ? string.Join(", ", seen) : "nothing solid")}");
        }

        private static string? DockNamed(Vector3 pos)
        {
            foreach ((Vector3 p, float yaw, string n) in Elevator.DockList())
                if (Vector3.Distance(p, pos) < 1f) return n;
            return null;
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

    // Puts the wanted room in the lift cell, so a report about one room is tested in that room
    // instead of rolled for.
    [HarmonyPatch(typeof(LevelGenerator), "PlanModule")]
    internal static class ForceLiftRoomPatch
    {
        internal static bool Next;

        private static void Prefix([HarmonyArgument(0)] int x, [HarmonyArgument(1)] int y, [HarmonyArgument(4)] Module.Type type)
        {
            Next = Probe.WantRoom != null && Session.Active && type == Module.Type.Normal && x == Session.LiftX && y == Session.LiftY;
        }

        private static void Postfix()
        {
            Next = false;
        }
    }

    [HarmonyPatch(typeof(LevelGenerator), "PickModule")]
    internal static class ForceLiftPickPatch
    {
        private static bool Prefix(LevelGenerator __instance, ref PrefabRef __result)
        {
            if (!ForceLiftRoomPatch.Next || Probe.WantRoom == null)
                return true;
            ForceLiftRoomPatch.Next = false;
            foreach (List<PrefabRef> list in new[] { __instance.Level.ModulesNormal1, __instance.Level.ModulesNormal2, __instance.Level.ModulesNormal3 })
            {
                if (list == null)
                    continue;
                foreach (PrefabRef pr in list)
                {
                    if (pr != null && pr.IsValid() && (pr.PrefabName ?? "").Contains(Probe.WantRoom))
                    {
                        __result = pr;
                        return false;
                    }
                }
            }
            return true;
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
            // Deep enough into a run for several extraction points and the bigger grid.
            if (__instance.levelsCompleted < 12)
                __instance.levelsCompleted = 12;
            return false;
        }
    }
}
