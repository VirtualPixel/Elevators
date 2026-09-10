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
            log.LogInfo($"[Probe] {name} opening scan: {found} finding(s)");
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
                Upstairs.Patches.MapFloorsPatch.Viewing = f;
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                var layers = new System.Text.StringBuilder();
                foreach (MapLayer layer in map.Layers)
                    layers.Append($" L{layer.layer}@{layer.transform.localPosition.y:0.0}({layer.transform.childCount})");
                Transform? label = cam != null ? cam.transform.Find("Upstairs floor") : null;
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
                log.LogInfo($"[Probe] {name} map floor {f} '{Upstairs.Patches.MapFloorsPatch.ViewingName}' playerLayer={map.PlayerLayer} label {labelInfo} cull={(cam != null ? cam.cullingMask : 0)} over={(map.OverLayerParent != null ? map.OverLayerParent.name + "/" + map.OverLayerParent.childCount + "@" + map.OverLayerParent.localPosition.y.ToString("0.0") : "none")} marks:{marks} layers:{layers}");
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
                Upstairs.Patches.MapFloorsPatch.Viewing = mine == 0 ? 1 : 0;
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
                    Vector3 spot = target.transform.position + Vector3.up * 0.3f;
                    Place(PlayerAvatar.instance, spot);
                    yield return new WaitForSecondsRealtime(2.5f);
                    map.debugActive = false;
                    yield return new WaitForSecondsRealtime(2.5f);
                    bool cleared = mm == null || mm.transform.localScale.x < 0.05f;
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
            Place(me, ground + Vector3.up * 0.6f);
            yield return new WaitForSecondsRealtime(1.5f);
            if (!Elevator.IsInside(me.transform.position))
            {
                Place(me, ground + Vector3.up * 0.6f);
                yield return new WaitForSecondsRealtime(1.5f);
            }
            int upDock = DockNamed("upstairs");
            // What the grabber does: the hit's transform must carry the StaticGrabObject.
            foreach (Elevator.LiftButton b in Object.FindObjectsOfType<Elevator.LiftButton>())
            {
                Vector3 axis = b.transform.up;
                bool hit = Physics.Raycast(b.transform.position + axis * 0.8f, -axis, out RaycastHit h, 1.5f, ~0, QueryTriggerInteraction.Ignore);
                log.LogInfo($"[Probe] {name} ray at {b.Kind}/{b.Dock}: hit={(hit ? h.transform.name : "nothing")} static={(hit && h.transform.GetComponent<StaticGrabObject>() != null)} rb={(hit && h.rigidbody != null ? h.rigidbody.name : "none")}");
            }
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
                    UpstairsNet.RequestRide(upDock);
                }
            }
            else
                UpstairsNet.RequestRide(upDock);
            yield return new WaitForSecondsRealtime(1.5f);
            log.LogInfo($"[Probe] {name} hum during ride: {Elevator.HumStatus()}");
            yield return RideTelemetry(name, me, "UP");
            log.LogInfo($"[Probe] {name} hum after ride: {Elevator.HumStatus()}");
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR UP", me.transform.position, up);
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
            UpstairsNet.RequestRide(DockNamed("ground"));
            yield return new WaitForSecondsRealtime(1.5f);
            yield return RideTelemetry(name, me, "DOWN", cargo);
            yield return new WaitForSecondsRealtime(1f);
            Verdict(name, "ELEVATOR DOWN", me.transform.position, ground);
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
        private IEnumerator RideTelemetry(string name, PlayerAvatar me, string what, ValuableObject? cargo = null)
        {
            float riderMin = 99f, riderMax = -99f, cargoMin = 99f, cargoMax = -99f, prev = float.NaN, jitter = 0f, camMin = 99f, camMax = -99f;
            int samples = 0;
            float waited = 0f;
            while (Elevator.Moving && waited < 30f)
            {
                yield return new WaitForFixedUpdate();
                waited += Time.fixedDeltaTime;
                if (Elevator.CarVelocityY == 0f) continue;
                float rider = PlayerController.instance.rb.position.y - Elevator.FloorY;
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
            log.LogInfo($"[Probe] {name} RIDE {what} interp player={PlayerController.instance.rb.interpolation} samples={samples} rider above floor min={riderMin:0.00} max={riderMax:0.00} stepJitter={jitter:0.000} camAboveBody={camMin:0.00}..{camMax:0.00}{(cargo != null ? $" cargo min={cargoMin:0.00} max={cargoMax:0.00} inside={Elevator.IsInside(cargo.transform.position)}" : "")}");
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
            // Deep enough into a run for several extraction points and the bigger grid.
            if (__instance.levelsCompleted < 12)
                __instance.levelsCompleted = 12;
            return false;
        }
    }
}
