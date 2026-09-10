using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Elevators.Services;

namespace ElevatorsProbe
{
    [BepInPlugin("Vippy.ElevatorsProbe", "ElevatorsProbe", "0.1.0")]
    [BepInDependency("Vippy.Elevators")]
    public class Probe : BaseUnityPlugin
    {
        private static string[] levels = { "Museum", "Manor", "Arctic", "Wizard" };

        private ManualLogSource log = null!;
        internal static Level? Forced;

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
            EnemyParent? monster = null;
            if (EnemyDirector.instance != null)
            {
                foreach (EnemyParent p in EnemyDirector.instance.enemiesSpawned)
                {
                    if (p != null && p.Spawned && p.Enemy != null && p.Enemy.HasStateInvestigate && p.Enemy.GetComponentInChildren<EnemyThinMan>(true) == null)
                    {
                        monster = p;
                        break;
                    }
                }
            }
            if (monster == null)
            {
                log.LogWarning($"[Probe] {name} MONSTER: nothing spawned to test with");
                yield break;
            }
            int groundDock = DockNamed("ground");
            Vector3 groundLanding = Elevator.LandingOf("ground");
            Vector3 upLanding = Elevator.LandingOf("upstairs");
            Place(me, ground + Vector3.up * 0.6f);
            yield return new WaitForSecondsRealtime(1f);
            monster.Enemy.EnemyTeleported(groundLanding);
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
            monster.Enemy.StateInvestigate.Set(ground, false);
            float best = 99f, t = 0f;
            bool inside = false;
            while (t < 15f && !inside)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                t += 0.5f;
                if (monster == null || !monster.Spawned || monster.Enemy == null)
                    break;
                Vector3 at = monster.Enemy.transform.position;
                best = Mathf.Min(best, Vector3.Distance(at, ground));
                inside = Elevator.IsInside(at);
                if (t % 4f < 0.5f)
                    monster.Enemy.StateInvestigate.Set(ground, false);
            }
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
            monster.Enemy.EnemyTeleported(upLanding);
            yield return new WaitForSecondsRealtime(1f);
            float busy = 0f;
            while (Elevator.Patrolling && busy < 60f) { yield return new WaitForSecondsRealtime(1f); busy += 1f; }
            Elevator.StartPatrol(monster, upDock, groundDock);
            float pt = 0f;
            bool done = false;
            while (pt < 70f && !done)
            {
                yield return new WaitForSecondsRealtime(1f);
                pt += 1f;
                if (monster == null || monster.Enemy == null)
                    break;
                done = Elevator.CurrentDock == groundDock && !Elevator.Moving && Vector3.Distance(monster.Enemy.transform.position, groundLanding) < 5f;
            }
            log.LogInfo($"[Probe] {name} MONSTER PATROL {(done ? "ok" : "FAIL")}: after {pt:0}s car at dock {Elevator.CurrentDock} moving={Elevator.Moving}, {monster?.enemyName} at {monster?.Enemy?.transform.position}, ground landing {groundLanding}");
            yield return new WaitForSecondsRealtime(1f);
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
            log.LogInfo($"[Probe] {name} NAV axis car->room {axis} (0.3 m steps from the car centre out through the doors); in the gaps: {(seen.Count > 0 ? string.Join(", ", seen) : "nothing solid")}");
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
