using System.Collections.Generic;
using Photon.Pun;
using REPOLib.Modules;
using UnityEngine;
using Upstairs.UI;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // The elevator itself, on every client. One car per level, the Museum's own carriage lifted
    // out of its module prefab, with a light and two buttons inside and a call button at each
    // dock. It drives between the docks through a dark shaft at elevator speed, carrying whoever
    // stands in it. The host decides when it moves; every client plays the same trip.
    internal static class Elevator
    {
        private const string SourceModule = "Module - Museum - P - 3 - Elevator To Sub Floor";
        internal const string CarriageName = "Museum Elevator Carriage";
        private const float FloorOffset = 2.15f;
        private const float DoorSlide = 1.1f;
        private const float ApproachRange = 3.2f;
        private const float Speed = 8f;
        private const float Accel = 4f;
        private const float DoorTime = 0.5f;

        internal enum ButtonKind
        {
            Call,
            Up,
            Down,
            Hatch
        }

        // Drives the car from FixedUpdate so the player standing in it is moved in physics time.
        internal sealed class ElevatorBody : MonoBehaviour
        {
            private void FixedUpdate() => Step(Time.fixedDeltaTime);
        }

        // A pressable thing. Carries which dock it calls the car to, or which way to go from inside.
        // The real buttons are network objects the host spawns; each one arrives with its kind and dock
        // and clips itself onto the matching slot of the car or a dock post.
        internal sealed class LiftButton : MonoBehaviour, IPunInstantiateMagicCallback
        {
            public ButtonKind Kind;
            public int Dock;
            public bool Attached;

            public float LastPress = -10f;

            public void OnPhotonInstantiate(PhotonMessageInfo info)
            {
                object[] data = info.photonView.InstantiationData;
                if (data == null || data.Length < 2)
                    return;
                Kind = (ButtonKind)(int)data[0];
                Dock = (int)data[1];
                // The grab area fires on the host when a beam takes hold inside it, the way the
                // shop's reroll button is pressed. Listeners are not cloned, so they go on here.
                Wire();
                Attach(this);
            }

            private bool wired;

            public void Wire()
            {
                if (wired)
                    return;
                wired = true;
                PhysGrabObjectGrabArea area = GetComponent<PhysGrabObjectGrabArea>();
                if (area == null)
                    return;
                foreach (PhysGrabObjectGrabArea.GrabArea grabArea in area.grabAreas)
                    grabArea.grabAreaEventOnStart.AddListener(() => PressedOnHost(this, area.GetLatestGrabber()));
            }
        }

        private sealed class Slot
        {
            public Transform Anchor = null!;
            public ButtonKind Kind;
            public int Dock;
        }

        private static readonly List<Slot> slots = new List<Slot>();
        private static readonly List<LiftButton> loose = new List<LiftButton>();
        private static GameObject? buttonTemplate;
        private static PrefabRef? buttonRef;
        private static bool shopButton;
        private const string ButtonPrefabId = "UpstairsButton";
        private const string ButtonMeshName = "Museum Elevator Button";
        private const string ShopModulePath = "level/shop/modules/Module - Shop - N - Center Extract";
        private const string ShopButtonName = "Shop Button";
        private const float ShopButtonScale = 0.55f;
        private static GameObject? hatchTemplate;
        private static PrefabRef? hatchRef;
        private const string HatchPrefabId = "UpstairsHatch";
        private const string HatchHolderName = "Elevator Hatch";
        private const string HatchPartName = "Hinge Template";
        private const float HatchSwing = 110f;
        private const float HatchSendRate = 0.1f;
        private static bool hatchHeld;
        private static float hatchSent;

        private sealed class Dock
        {
            public Vector3 Position;
            public float Yaw;
            public string Name = "";
            // Set when the dock sits inside a real elevator shaft: the room's own doors, reused as
            // landing doors that only open while the car is here.
            public Transform? LandingL;
            public Transform? LandingR;
            public Vector3 LandingClosedL;
            public Vector3 LandingClosedR;
            public float LandingOpen;
            public bool InShaft;
        }

        private static readonly List<Dock> docks = new List<Dock>();
        private static GameObject? carriageAsset;
        private static ExtractionPoint? sounds;

        private static GameObject? car;
        private static Transform? doorL;
        private static Transform? doorR;
        private static Vector3 closedL;
        private static Vector3 closedR;
        private static float doorOpen;
        private static bool doorsHeld;

        private static int atDock = -1;
        private static int toDock = -1;
        private static float travelT;
        private static float travelLength;
        private static Vector3 travelFrom;
        private static Vector3 travelTo;
        private static float doorWait;
        private static float landWait;
        private const float LandPause = 0.9f;
        private static Sound? landThud;
        private static Vector3 lastCarPos;
        private static bool localRiding;
        private static AudioSource? hum;
        private static Sound? humLoop;
        private static Rigidbody? body;
        private static float cageHalfWidth = 1.7f;
        private static Transform? hatch;
        private static Quaternion hatchClosed;
        private static float hatchOpen;
        private static bool hatchWantOpen;

        public static bool Built => car != null;
        public static bool Moving => toDock >= 0;
        public static int CurrentDock => atDock;
        public static int DockCount => docks.Count;

        // The button every client can grab: the Museum's wall plate with the game's own
        // StaticGrabObject and a PhotonView, registered with REPOLib so the host can spawn it as a
        // room object and the grabber treats it like the extraction button. Built once per session.
        public static void EnsureButtonPrefab()
        {
            if (buttonTemplate != null)
                return;
            GameObject? template = ShopButtonTemplate();
            shopButton = template != null;
            template ??= PlateTemplate();
            template.name = ButtonPrefabId;
            template.SetActive(false);
            Object.DontDestroyOnLoad(template);
            template.AddComponent<LiftButton>();
            buttonTemplate = template;
            buttonRef = NetworkPrefabs.RegisterNetworkPrefab(ButtonPrefabId, template);
            Plugin.LogInfo($"[Elevator] button prefab registered ({(shopButton ? "shop button" : "Museum plate")}), ref {(buttonRef != null ? "ok" : "MISSING")}");
            EnsureHatchPrefab();
        }

        // The roof hatch as a thing you take hold of and swing: the Museum's hatch plate with its
        // hinge stripped, one box to grab, and the game's StaticGrabObject so the beam sticks to
        // it. The swing itself is driven from the grabber's pull, since a joint can't ride a car.
        private static void EnsureHatchPrefab()
        {
            if (hatchTemplate != null)
                return;
            GameObject? holder = FindAsset(HatchHolderName);
            Transform? part = holder != null ? holder.transform.Find(HatchPartName) : null;
            if (part == null)
            {
                Plugin.LogInfo("[Elevator] no hatch plate to borrow, the roof hatch stays fixed");
                return;
            }
            GameObject template = Object.Instantiate(part.gameObject);
            template.name = HatchPrefabId;
            template.SetActive(false);
            Object.DontDestroyOnLoad(template);
            StripScripts(template);
            int layer = LayerMask.NameToLayer("StaticGrabObject");
            foreach (Transform t in template.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
            foreach (Collider c in template.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
            var box = template.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0f, 0.8f);
            box.size = new Vector3(1.46f, 0.08f, 1.6f);
            var grab = template.AddComponent<StaticGrabObject>();
            grab.colliderTransform = template.transform;
            grab.uiPosition = template.transform;
            grab.hoverText = "Roof hatch";
            template.AddComponent<PhotonView>();
            OwnBody(template);
            template.AddComponent<LiftButton>();
            hatchTemplate = template;
            hatchRef = NetworkPrefabs.RegisterNetworkPrefab(HatchPrefabId, template);
            Plugin.LogInfo($"[Elevator] hatch prefab registered, ref {(hatchRef != null ? "ok" : "MISSING")}");
        }

        // The game's own pressable button, the one the shop's reroll sits on: a real grab body with
        // a grab area, its own light, and the grabber's beam already knows what to do with it. The
        // shop module's press wiring is dropped for a fresh grab area the buttons wire on arrival.
        private static GameObject? ShopButtonTemplate()
        {
            GameObject? module = Resources.Load<GameObject>(ShopModulePath);
            if (module == null)
                return null;
            Transform? source = null;
            foreach (Transform t in module.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == ShopButtonName)
                {
                    source = t;
                    break;
                }
            }
            if (source == null)
                return null;
            GameObject button = Object.Instantiate(source.gameObject);
            button.SetActive(false);
            button.transform.localScale = Vector3.one;
            foreach (PhysGrabObjectGrabArea old in button.GetComponents<PhysGrabObjectGrabArea>())
                Object.DestroyImmediate(old);
            foreach (PhotonView old in button.GetComponentsInChildren<PhotonView>(true))
                Object.DestroyImmediate(old);
            Transform? areaBox = null;
            foreach (Transform t in button.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith("Grab Area"))
                    areaBox = t;
            }
            StaticGrabObject grab = button.GetComponent<StaticGrabObject>() ?? button.AddComponent<StaticGrabObject>();
            grab.colliderTransform = button.transform;
            grab.uiPosition = button.transform;
            grab.hoverText = "Elevator";
            button.AddComponent<PhotonView>();
            OwnBody(button);
            if (areaBox != null)
            {
                var area = button.AddComponent<PhysGrabObjectGrabArea>();
                area.grabAreas.Add(new PhysGrabObjectGrabArea.GrabArea { grabAreaTransform = areaBox });
            }
            return button;
        }

        // The grabber takes the hit's rigidbody as the thing it grabbed. Inside the car every
        // collider belongs to the car's body, so a button needs a body of its own to be seen.
        private static void OwnBody(GameObject go)
        {
            // The hatch plate came with a body of its own; a destroyed one lingers until the
            // end of the frame and a second cannot be added beside it.
            Rigidbody rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        // Fallback when the shop module is not there to borrow from: the Museum's wall plate with
        // the game's StaticGrabObject on a box.
        private static GameObject PlateTemplate()
        {
            var template = new GameObject(ButtonPrefabId);
            template.SetActive(false);
            int layer = LayerMask.NameToLayer("StaticGrabObject");
            GameObject? plate = FindAsset(ButtonMeshName);
            Vector3 size = new Vector3(0.3f, 0.3f, 0.1f);
            if (plate != null)
            {
                GameObject mesh = Object.Instantiate(plate, template.transform);
                StripScripts(mesh);
                mesh.transform.localPosition = Vector3.zero;
                // The plate faces -x where the Museum hangs it; turned so it faces the template's +z.
                mesh.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                MeshFilter filter = mesh.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    Bounds b = filter.sharedMesh.bounds;
                    size = new Vector3(b.size.z + 0.04f, b.size.y + 0.04f, Mathf.Max(0.08f, b.size.x + 0.04f));
                }
                foreach (Transform t in mesh.GetComponentsInChildren<Transform>(true))
                    t.gameObject.layer = layer;
            }
            else
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(cube.GetComponent<Collider>());
                cube.transform.SetParent(template.transform, false);
                cube.transform.localScale = new Vector3(0.25f, 0.25f, 0.05f);
                cube.layer = layer;
            }
            template.layer = layer;
            var box = template.AddComponent<BoxCollider>();
            box.size = size;
            var grab = template.AddComponent<StaticGrabObject>();
            grab.colliderTransform = template.transform;
            grab.uiPosition = template.transform;
            grab.hoverText = "Elevator";
            template.AddComponent<PhotonView>();
            OwnBody(template);
            return template;
        }

        // A spawned button finds its slot; before the car exists it waits.
        private static void Attach(LiftButton button)
        {
            if (button == null || button.Attached)
                return;
            foreach (Slot slot in slots)
            {
                if (slot.Kind != button.Kind || slot.Dock != button.Dock)
                    continue;
                button.transform.SetParent(slot.Anchor, false);
                button.transform.localPosition = Vector3.zero;
                bool isHatch = button.Kind == ButtonKind.Hatch;
                button.transform.localRotation = shopButton && !isHatch ? Quaternion.FromToRotation(Vector3.up, Vector3.forward) : Quaternion.identity;
                button.transform.localScale = shopButton && !isHatch ? Vector3.one * ShopButtonScale : Vector3.one;
                button.Attached = true;
                if (isHatch)
                {
                    hatch = button.transform;
                    hatchClosed = Quaternion.identity;
                    hatch.localRotation = Quaternion.Euler(-HatchSwing * hatchOpen, 0f, 0f);
                }
                StaticGrabObject grab = button.GetComponent<StaticGrabObject>();
                if (grab != null)
                    grab.hoverText = button.Kind switch { ButtonKind.Up => "Up", ButtonKind.Down => "Down", ButtonKind.Hatch => "Roof hatch", _ => "Call the elevator" };
                loose.Remove(button);
                return;
            }
            if (!loose.Contains(button))
                loose.Add(button);
        }

        // Host only: one network button per slot.
        private static void SpawnButtons()
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() || buttonRef == null)
                return;
            int spawned = 0;
            foreach (Slot slot in slots)
            {
                PrefabRef? prefab = slot.Kind == ButtonKind.Hatch ? hatchRef : buttonRef;
                if (prefab == null)
                    continue;
                GameObject? go = NetworkPrefabs.SpawnNetworkPrefab(prefab, slot.Anchor.position, slot.Anchor.rotation, 0, new object[] { (int)slot.Kind, slot.Dock });
                if (go == null)
                    continue;
                // Offline the spawn is a plain copy of the sleeping template, so wake it here.
                if (!go.activeSelf)
                    go.SetActive(true);
                LiftButton button = go.GetComponent<LiftButton>();
                button.Kind = slot.Kind;
                button.Dock = slot.Dock;
                button.Wire();
                Attach(button);
                spawned++;
            }
            Plugin.LogInfo($"[Elevator] {spawned} grabbable buttons spawned");
        }

        // Called from the grabber's own client the moment it takes hold of a button.
        public static void Grabbed(StaticGrabObject grab, PhysGrabber player)
        {
            LiftButton button = grab.GetComponent<LiftButton>();
            if (button == null || player == null || !player.isLocal)
                return;
            Plugin.LogInfo($"[Elevator] grabbed {button.Kind} (dock {button.Dock})");
            Press(button);
            Ride(button);
        }

        // The host's grab area saw a beam take hold: the press that counts, plus the click for
        // everyone who did not press it themselves.
        private static void PressedOnHost(LiftButton button, PlayerAvatar? grabber)
        {
            if (button == null || !SemiFunc.IsMasterClientOrSingleplayer())
                return;
            int grabberView = grabber != null && grabber.photonView != null ? grabber.photonView.ViewID : -1;
            Plugin.LogInfo($"[Elevator] grab area press {button.Kind} (dock {button.Dock})");
            // Offline the buttons carry no network id, so the host presses its own copy here and
            // only the other clients are told which one.
            Press(button);
            if (SemiFunc.IsMultiplayer())
                UpstairsNet.SendPress(button.GetComponent<PhotonView>()?.ViewID ?? -1, grabberView);
            Ride(button);
        }

        // Everyone: the click and the squash. The presser's own client already played it on the
        // grab, and the short dedupe in Press keeps that from doubling.
        public static void PlayPress(int buttonView, int grabberView)
        {
            PhotonView view = PhotonView.Find(buttonView);
            LiftButton button = view != null ? view.GetComponent<LiftButton>() : null;
            if (button == null)
                return;
            Press(button);
        }

        private static Sound? standPress;
        private static bool standPressLooked;

        private static void Press(LiftButton button)
        {
            if (Time.time - button.LastPress < 0.3f)
                return;
            button.LastPress = Time.time;
            Play(sounds?.soundButton, button.transform.position);
            // The upgrade stand's press has the weight the extraction click lacks.
            if (!standPressLooked)
            {
                standPressLooked = true;
                GameObject? stands = Resources.Load<GameObject>("level/shop/modules/Module - Shop - N - Corner Stands");
                UpgradeStand? stand = stands != null ? stands.GetComponentInChildren<UpgradeStand>(true) : null;
                standPress = stand != null ? stand.soundButtonPress : null;
            }
            Play(standPress, button.transform.position);
            Plugin.Instance.StartCoroutine(Squash(button.transform));
        }

        private static void Ride(LiftButton button)
        {
            int target = button.Kind switch
            {
                ButtonKind.Call => button.Dock,
                ButtonKind.Up => Neighbour(above: true),
                ButtonKind.Down => Neighbour(above: false),
                _ => -1
            };
            if (target >= 0)
                UpstairsNet.RequestRide(target);
        }

        private static System.Collections.IEnumerator Squash(Transform plate)
        {
            if (!shopButton)
            {
                Vector3 rest = plate.localScale;
                plate.localScale = new Vector3(rest.x, rest.y, rest.z * 0.4f);
                yield return new WaitForSeconds(0.15f);
                if (plate != null)
                    plate.localScale = rest;
                yield break;
            }
            // The shop button's cap travels down its own axis and springs back.
            Transform cap = plate.Find("Shop Button Visual") ?? plate;
            Vector3 up = cap.localPosition;
            Vector3 down = up - Vector3.up * 0.09f;
            float t = 0f;
            while (t < 1f && cap != null)
            {
                t += Time.deltaTime / 0.08f;
                cap.localPosition = Vector3.Lerp(up, down, Mathf.Clamp01(t));
                yield return null;
            }
            yield return new WaitForSeconds(0.12f);
            t = 0f;
            while (t < 1f && cap != null)
            {
                t += Time.deltaTime / 0.15f;
                cap.localPosition = Vector3.Lerp(down, up, Mathf.Clamp01(t));
                yield return null;
            }
            if (cap != null)
                cap.localPosition = up;
        }

        public static void Clear()
        {
            slots.Clear();
            loose.Clear();
            if (car != null)
                Object.Destroy(car.transform.parent != null ? car.transform.parent.gameObject : car);
            foreach (GameObject g in GameObject.FindGameObjectsWithTag("Untagged"))
            {
                if (g.name == "Upstairs dock" || g.name == "Upstairs shaft")
                    Object.Destroy(g);
            }
            car = null;
            doorL = doorR = null;
            hum = null;
            humLoop = null;
            landWait = 0f;
            hatch = null;
            hatchOpen = 0f;
            hatchWantOpen = false;
            hatchHeld = false;
            docks.Clear();
            atDock = -1;
            toDock = -1;
            localRiding = false;
            sounds = null;
        }

        public static void Build(IReadOnlyList<(Vector3 pad, float yaw, string name)> pads)
        {
            Clear();
            if (pads.Count < 2)
                return;
            carriageAsset ??= FindAsset(CarriageName);
            if (carriageAsset == null)
            {
                Plugin.LogAlways("[Elevator] the Museum elevator car is not in this build, no elevator this level");
                return;
            }
            sounds = Object.FindObjectOfType<ExtractionPoint>();
            Plugin.LogInfo($"[Elevator] one-shot sounds {(sounds != null ? "from the extraction point" : "MISSING, no extraction point found")}");
            foreach ((Vector3 pad, float yaw, string name) in pads)
                docks.Add(new Dock { Position = pad, Yaw = yaw, Name = name });

            int ground = docks.FindIndex(d => d.Name == "ground");
            if (ground < 0)
                ground = 0;
            BuildCar(docks[ground]);
            atDock = ground;
            for (int i = 0; i < docks.Count; i++)
            {
                BuildCallButton(i);
                OpenShaft(docks[i]);
                if (docks[i].LandingL == null && docks[i].LandingR == null)
                    BuildLandingDoors(docks[i]);
                Vector3 from = docks[i].Position + Vector3.up * 1f;
                string under = Physics.Raycast(from, Vector3.down, out RaycastHit floorHit, 3f, ~0, QueryTriggerInteraction.Ignore) ? $"{floorHit.collider.name} {floorHit.distance:0.00} m down" : "NOTHING within 3 m";
                Plugin.LogInfo($"[Elevator] under the {docks[i].Name} dock: {under}");
            }
            BuildShaft();
            foreach (LiftButton waiting in loose.ToArray())
                Attach(waiting);
            SpawnButtons();
            Plugin.LogInfo($"[Elevator] car at {docks[ground].Name}, {docks.Count} docks");
        }

        private static void BuildCar(Dock dock)
        {
            var holder = new GameObject("Upstairs elevator");
            holder.SetActive(false);
            GameObject body = Object.Instantiate(carriageAsset!, holder.transform);
            body.name = CarriageName;
            StripScripts(body);
            foreach (Transform child in body.transform)
            {
                if (child.name.StartsWith(HatchHolderName))
                {
                    // The roof hatch is a small door you take hold of and swing. The plate that came
                    // with the carriage makes way for the grabbable one the host spawns in its place.
                    Transform? plate = child.Find(HatchPartName);
                    if (plate != null && hatchRef != null)
                    {
                        var anchor = new GameObject("slot Hatch").transform;
                        anchor.SetParent(child, false);
                        anchor.localPosition = plate.localPosition;
                        anchor.localRotation = plate.localRotation;
                        slots.Add(new Slot { Anchor = anchor, Kind = ButtonKind.Hatch, Dock = -1 });
                        plate.gameObject.SetActive(false);
                    }
                    else if (plate != null)
                    {
                        hatch = plate;
                        hatchClosed = plate.localRotation;
                    }
                }
                else if (child.name.StartsWith("Invisible Wall"))
                    child.gameObject.SetActive(false);
                else if (child.name.StartsWith("Audio Loop") && hum == null)
                    hum = child.GetComponent<AudioSource>();
                else if (child.name.StartsWith("Audio Loop"))
                    child.gameObject.SetActive(false);
            }
            if (hum != null)
            {
                // The carriage's own loop is close to silent; a door motor from the Arctic
                // level is the sound an elevator makes.
                AudioClip clip = MotorClip() ?? hum.clip;
                hum.Stop();
                hum.gameObject.SetActive(false);
                var motor = new GameObject("motor").AddComponent<AudioSource>();
                motor.transform.SetParent(body.transform, false);
                motor.transform.localPosition = new Vector3(0f, 1.8f, 0.3f);
                motor.clip = clip;
                motor.loop = true;
                motor.playOnAwake = false;
                motor.spatialBlend = 1f;
                motor.rolloffMode = AudioRolloffMode.Logarithmic;
                motor.minDistance = 3f;
                motor.maxDistance = 30f;
                motor.volume = 0f;
                // Routed like the game's own one-shots, which are heard, rather than the carriage
                // loop's group, which was not.
                AudioSource? proto = AudioManager.instance != null && AudioManager.instance.AudioDefault != null ? AudioManager.instance.AudioDefault.GetComponent<AudioSource>() : null;
                motor.outputAudioMixerGroup = proto != null && proto.outputAudioMixerGroup != null ? proto.outputAudioMixerGroup : (AudioManager.instance != null ? AudioManager.instance.SoundMasterGroup : hum.outputAudioMixerGroup);
                if (proto != null)
                {
                    motor.rolloffMode = proto.rolloffMode;
                    motor.minDistance = Mathf.Max(2f, proto.minDistance);
                    motor.maxDistance = Mathf.Max(20f, proto.maxDistance);
                }
                hum = motor;
                humLoop = new Sound
                {
                    Sounds = new[] { clip },
                    Source = motor,
                    Volume = 0.55f,
                    VolumeRandom = 0f,
                    Pitch = 1f,
                    PitchRandom = 0f,
                    SpatialBlend = 1f,
                    Doppler = 0f,
                    ReverbMix = 1f,
                    FalloffMultiplier = 1f
                };
                Plugin.LogInfo($"[Elevator] motor clip: {(clip != null ? clip.name : "none")}, group {(motor.outputAudioMixerGroup != null ? motor.outputAudioMixerGroup.name : "none")}");
            }
            else
            {
                Plugin.LogInfo("[Elevator] the carriage has no audio loop child, the ride is silent");
            }
            Transform cables = body.transform.Find("Cables");
            if (cables != null)
            {
                for (int i = 0; i < cables.childCount; i++)
                    cables.GetChild(i).gameObject.SetActive(cables.GetChild(i).localPosition.y < 3f);
            }
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;

            var lamp = new GameObject("light").AddComponent<Light>();
            lamp.transform.SetParent(body.transform, false);
            lamp.transform.localPosition = new Vector3(0f, 1.6f, 0.3f);
            lamp.type = LightType.Point;
            lamp.color = new Color(1f, 0.86f, 0.62f);
            lamp.intensity = 1.6f;
            lamp.range = 5f;
            lamp.shadows = LightShadows.None;

            // Two buttons on the right hand wall just inside the doors, the same mesh the Museum uses by
            // its shaft, set off the cage's real width so they sit on the wall and not in it.
            MeshFilter cage = body.GetComponent<MeshFilter>();
            float wallX = cage != null && cage.sharedMesh != null ? cage.sharedMesh.bounds.extents.x - 0.1f : 1.05f;
            cageHalfWidth = wallX + 0.1f;
            // The panel's forward points into the car; buttons and labels stand proud of the plate along it.
            // Room for two shop buttons with a word under each.
            Transform panel = Panel(body.transform, new Vector3(wallX - 0.02f, -0.5f, -0.6f), Quaternion.LookRotation(Vector3.left), 0.9f);
            AddSlot(panel, new Vector3(0f, 0.28f, 0.02f), ButtonKind.Up, -1, "UP");
            AddSlot(panel, new Vector3(0f, -0.12f, 0.02f), ButtonKind.Down, -1, "DOWN");
            var panelLight = new GameObject("panel light").AddComponent<Light>();
            panelLight.transform.SetParent(body.transform, false);
            panelLight.transform.localPosition = new Vector3(wallX - 0.35f, -0.4f, -0.6f);
            panelLight.type = LightType.Point;
            panelLight.color = new Color(1f, 0.75f, 0.45f);
            panelLight.intensity = 0.9f;
            panelLight.range = 1.6f;
            panelLight.shadows = LightShadows.None;
            Plugin.LogInfo($"[Elevator] cage half width {wallX + 0.1f:0.00}, panel at x={wallX:0.00}");

            holder.transform.SetPositionAndRotation(dock.Position + Vector3.up * FloorOffset, Quaternion.Euler(0f, dock.Yaw, 0f));
            Rigidbody rb = holder.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            holder.AddComponent<ElevatorBody>();
            holder.SetActive(true);
            car = body;
            Elevator.body = rb;
            lastCarPos = holder.transform.position;
            doorL = body.transform.Find("Museum Elevator door (1)");
            doorR = body.transform.Find("Museum Elevator door");
            if (doorL != null) closedL = ClosedAt(doorL, left: true);
            if (doorR != null) closedR = ClosedAt(doorR, left: false);
            if (doorL != null) doorL.localPosition = closedL;
            if (doorR != null) doorR.localPosition = closedR;
            doorOpen = 0f;
        }

        // A dock inside the room's own shaft: the stuck carriage goes, its doors become landing doors,
        // and the shaft's caps go so the car can pass through top and bottom.
        private static void OpenShaft(Dock dock)
        {
            foreach (Module module in Object.FindObjectsOfType<Module>())
            {
                if (!module.name.StartsWith(SourceModule))
                    continue;
                Transform? carriage = null;
                foreach (Transform t in module.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == CarriageName)
                    {
                        carriage = t;
                        break;
                    }
                }
                if (carriage == null)
                    continue;
                Vector3 d = carriage.position - dock.Position;
                float above = dock.Position.y - module.transform.position.y;
                if (new Vector2(d.x, d.z).magnitude > 1.5f || above > 1f || above < -12f)
                    continue;

                Transform shaft = carriage.parent;
                bool lowest = true;
                foreach (Dock other in docks)
                    if (other.Position.y < dock.Position.y - 1f) lowest = false;
                foreach (Transform child in shaft)
                {
                    if (child.name.StartsWith("Ceiling") || (!lowest && (child.name.StartsWith("Floor") || child.name.StartsWith("Hurt Collider"))))
                        child.gameObject.SetActive(false);
                }
                var landing = new GameObject("Upstairs dock");
                landing.transform.SetPositionAndRotation(dock.Position + Vector3.up * FloorOffset, Quaternion.Euler(0f, dock.Yaw, 0f));
                dock.LandingL = CopyDoor(carriage, "Museum Elevator door (1)", landing.transform);
                dock.LandingR = CopyDoor(carriage, "Museum Elevator door", landing.transform);
                // A hair in front of the car's own doors so the two pairs never share a plane, but
                // still inside the wall, so an open door slides behind the frame and not across
                // the wall plate and its button.
                if (dock.LandingL != null) { dock.LandingClosedL = ClosedAt(dock.LandingL, left: true) + Vector3.back * 0.05f; dock.LandingL.localPosition = dock.LandingClosedL; }
                if (dock.LandingR != null) { dock.LandingClosedR = ClosedAt(dock.LandingR, left: false) + Vector3.back * 0.05f; dock.LandingR.localPosition = dock.LandingClosedR; }
                dock.InShaft = true;
                carriage.gameObject.SetActive(false);
                Plugin.LogInfo($"[Elevator] {dock.Name} dock is inside the room's own shaft");
                return;
            }
        }

        // A pair of doors at the dock that only open while the car is standing there, so a wall the
        // room opened for the elevator is never a hole into the void.
        private static void BuildLandingDoors(Dock dock)
        {
            if (carriageAsset == null)
                return;
            var landing = new GameObject("Upstairs dock");
            landing.transform.SetPositionAndRotation(dock.Position + Vector3.up * FloorOffset, Quaternion.Euler(0f, dock.Yaw, 0f));
            dock.LandingL = CopyDoor(carriageAsset.transform, "Museum Elevator door (1)", landing.transform);
            dock.LandingR = CopyDoor(carriageAsset.transform, "Museum Elevator door", landing.transform);
            if (dock.LandingL != null) { dock.LandingClosedL = ClosedAt(dock.LandingL, left: true) + Vector3.back * 0.18f; dock.LandingL.localPosition = dock.LandingClosedL; }
            if (dock.LandingR != null) { dock.LandingClosedR = ClosedAt(dock.LandingR, left: false) + Vector3.back * 0.18f; dock.LandingR.localPosition = dock.LandingClosedR; }
        }

        private static Transform? CopyDoor(Transform carriage, string name, Transform parent)
        {
            Transform? door = carriage.Find(name);
            if (door == null)
                return null;
            GameObject copy = Object.Instantiate(door.gameObject, parent);
            StripScripts(copy);
            copy.transform.localRotation = door.localRotation;
            copy.transform.localPosition = door.localPosition + Vector3.back * 0.18f;
            return copy.transform;
        }

        // The prefab parks its doors ajar. Shut means the two inner edges meet in the middle, so the
        // closed spot comes from the door mesh's own width.
        private static Vector3 ClosedAt(Transform door, bool left)
        {
            Vector3 at = door.localPosition;
            MeshFilter filter = door.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                at.x = left ? -0.62f : 0.62f;
                return at;
            }
            Bounds b = filter.sharedMesh.bounds;
            float scale = door.localScale.x;
            float inner = left ? b.center.x + b.extents.x : b.center.x - b.extents.x;
            at.x = -inner * scale;
            return at;
        }

        // Where the Museum hangs its elevator plate: beside the doors, at eye height, on the wall.
        private static readonly Vector3 MuseumPlate = new Vector3(1.936f, 1.75f, -1.59f);

        private static void BuildCallButton(int index)
        {
            Dock dock = docks[index];
            Quaternion facing = Quaternion.Euler(0f, dock.Yaw, 0f);
            if (dock.InShaft)
            {
                // The room already has a plate on the wall; the button goes on it.
                var mount = new GameObject("Upstairs dock");
                mount.transform.SetPositionAndRotation(dock.Position + facing * MuseumPlate, facing);
                var face = new GameObject("face").transform;
                face.SetParent(mount.transform, false);
                face.localRotation = Quaternion.Euler(0f, 180f, 0f);
                AddSlot(face, new Vector3(0f, 0f, 0.03f), ButtonKind.Call, index, "CALL");
                return;
            }
            // An unscaled stand holds the post cube and the panel, so the panel keeps its shape.
            var stand = new GameObject("Upstairs dock");
            stand.transform.SetPositionAndRotation(dock.Position + facing * new Vector3(1.9f, 0f, -1.6f), facing);
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "post";
            post.transform.SetParent(stand.transform, false);
            post.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            post.transform.localScale = new Vector3(0.12f, 1.2f, 0.12f);
            Renderer carRenderer = car!.GetComponent<Renderer>();
            if (carRenderer != null)
                post.GetComponent<Renderer>().sharedMaterial = carRenderer.sharedMaterial;
            // Faces the room, which is the car's -z.
            Transform plate = Panel(stand.transform, new Vector3(0f, 1.1f, -0.08f), Quaternion.Euler(0f, 180f, 0f), 0.44f);
            AddSlot(plate, new Vector3(0f, 0.08f, 0.02f), ButtonKind.Call, index, "CALL");
            var glow = new GameObject("call light").AddComponent<Light>();
            glow.transform.SetParent(stand.transform, false);
            glow.transform.localPosition = new Vector3(0f, 0.6f, -1.2f);
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.75f, 0.45f);
            glow.intensity = 0.8f;
            glow.range = 1.8f;
            glow.shadows = LightShadows.None;
        }

        private static Material? plateMaterial;

        // A dark plate with round lit buttons and a word under each, the way an elevator panel reads.
        private static Transform Panel(Transform parent, Vector3 localPos, Quaternion localRot, float height)
        {
            plateMaterial ??= new Material(Shader.Find("Sprites/Default")) { color = new Color(0.16f, 0.16f, 0.18f) };
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Upstairs panel";
            Object.Destroy(plate.GetComponent<Collider>());
            plate.transform.SetParent(parent, false);
            plate.transform.localPosition = localPos;
            plate.transform.localRotation = localRot;
            plate.transform.localScale = new Vector3(0.28f, height, 0.03f);
            plate.GetComponent<Renderer>().sharedMaterial = plateMaterial;
            var holder = new GameObject("face").transform;
            holder.SetParent(parent, false);
            holder.localPosition = localPos;
            holder.localRotation = localRot;
            return holder;
        }

        // Where a network button will sit once the host spawns it, with its word underneath.
        private static void AddSlot(Transform panel, Vector3 localPos, ButtonKind kind, int dock, string label)
        {
            var anchor = new GameObject("slot " + kind).transform;
            anchor.SetParent(panel, false);
            anchor.localPosition = localPos;
            anchor.localRotation = Quaternion.identity;
            slots.Add(new Slot { Anchor = anchor, Kind = kind, Dock = dock });

            Transform text = NativeText.CreateWorld(panel, label, 0.22f);
            text.localPosition = localPos + new Vector3(0f, -0.17f, 0f);
            text.localRotation = Quaternion.Euler(0f, 180f, 0f);
        }

        // A dark box around the path in the gap between each pair of floors, faces turned inward,
        // so the ride is a shaft and not a look at the void. It stops short of the docks, so a car
        // standing in a room is just a car.
        private static Material? shaftMaterial;

        private const float ShaftBelow = 14f;

        private static void BuildShaft()
        {
            var sorted = new List<Dock>(docks);
            sorted.Sort((a, b) => a.Position.y.CompareTo(b.Position.y));
            shaftMaterial ??= new Material(Shader.Find("Sprites/Default")) { color = new Color(0.04f, 0.04f, 0.05f) };
            Dock low = sorted[0];
            Dock high = sorted[sorted.Count - 1];
            float bottom = low.Position.y - ShaftBelow;
            float top = high.Position.y + 4.8f;
            Quaternion facing = Quaternion.Euler(0f, low.Yaw, 0f);
            // The box sits a touch behind the car so its near face lines up with the wall the room
            // opened, and the doorway looks straight down a shaft.
            Vector3 centre = (low.Position + high.Position) * 0.5f + facing * new Vector3(0f, 0f, 1.05f);
            var shaft = new GameObject("Upstairs shaft");
            shaft.transform.SetPositionAndRotation(new Vector3(centre.x, (bottom + top) * 0.5f, centre.z), facing);
            float mid = (bottom + top) * 0.5f;
            for (int side = 0; side < 4; side++)
            {
                Quaternion turn = Quaternion.Euler(0f, 90f * side, 0f);
                // Side 2 is the door side (car doors face -z). It is built in pieces that stop at each
                // dock, so an open door looks into the room and not at a black wall.
                if (side == 2)
                {
                    float from = bottom;
                    foreach (Dock d in sorted)
                    {
                        Piece(shaft.transform, turn, from, d.Position.y - 0.2f, mid);
                        from = d.Position.y + 4.6f;
                    }
                    Piece(shaft.transform, turn, from, top, mid);
                    continue;
                }
                Piece(shaft.transform, turn, bottom, top, mid);
            }
            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(shaft.transform, false);
            floor.transform.localPosition = new Vector3(0f, bottom - (bottom + top) * 0.5f, 0f);
            floor.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            floor.transform.localScale = new Vector3(5.2f, 5.2f, 1f);
            floor.GetComponent<Renderer>().sharedMaterial = shaftMaterial;
            BuildPit(new Vector3(centre.x, bottom + 1.5f, centre.z), facing);
        }

        private static void Piece(Transform shaft, Quaternion turn, float from, float to, float mid)
        {
            if (to - from < 0.3f)
                return;
            var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(wall.GetComponent<Collider>());
            wall.transform.SetParent(shaft, false);
            wall.transform.localPosition = turn * new Vector3(0f, 0f, 2.6f) + Vector3.up * ((from + to) * 0.5f - mid);
            wall.transform.localRotation = turn * Quaternion.Euler(0f, 180f, 0f);
            wall.transform.localScale = new Vector3(5.2f, to - from, 1f);
            wall.GetComponent<Renderer>().sharedMaterial = shaftMaterial;
        }

        // The bottom of the shaft is the game's own death pit, borrowed from the Museum elevator
        // room, so falling out the bottom ends the way falling into any pit does.
        private static void BuildPit(Vector3 at, Quaternion facing)
        {
            Transform? source = carriageAsset != null ? carriageAsset.transform.parent : null;
            Transform? pit = source != null ? source.Find("Hurt Collider - Death Pit") : null;
            if (pit == null)
                return;
            GameObject copy = Object.Instantiate(pit.gameObject);
            copy.name = "Upstairs shaft pit";
            copy.transform.SetPositionAndRotation(at, facing);
            copy.transform.localScale = Vector3.one;
            foreach (BoxCollider box in copy.GetComponentsInChildren<BoxCollider>(true))
            {
                box.center = Vector3.zero;
                box.size = new Vector3(5f, 2f, 5f);
            }
            copy.SetActive(true);
        }

        // Scripts, joints and bodies all go: the car is one kinematic body and everything on it is
        // a plain child that rides along. The hatch in particular came with a hinge of its own.
        private static void StripScripts(GameObject root)
        {
            foreach (MonoBehaviour script in root.GetComponentsInChildren<MonoBehaviour>(true))
                Object.DestroyImmediate(script);
            foreach (Joint joint in root.GetComponentsInChildren<Joint>(true))
                Object.DestroyImmediate(joint);
            foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);
        }

        private static AudioClip? motorClip;

        // The move loop of the Arctic level's doors, read off the first hinge that uses it in any
        // level's room list. Loaded once.
        private static AudioClip? MotorClip()
        {
            if (motorClip != null)
                return motorClip;
            RunManager rm = RunManager.instance;
            if (rm == null || rm.levels == null)
                return null;
            foreach (Level level in rm.levels)
            {
                foreach (List<PrefabRef> list in new[] { level.ModulesNormal1, level.ModulesPassage1, level.ModulesDeadEnd1 })
                {
                    if (list == null)
                        continue;
                    foreach (PrefabRef pr in list)
                    {
                        GameObject? prefab = pr != null && pr.IsValid() ? pr.Prefab : null;
                        if (prefab == null)
                            continue;
                        foreach (PhysGrabHinge hinge in prefab.GetComponentsInChildren<PhysGrabHinge>(true))
                        {
                            HingeAudio audio = hinge.hingeAudio;
                            if (audio == null || audio.moveLoop == null || audio.moveLoop.Sounds == null || audio.moveLoop.Sounds.Length == 0)
                                continue;
                            if (!audio.name.Contains("Arctic") && !audio.name.Contains("Container"))
                                continue;
                            motorClip = audio.moveLoop.Sounds[0];
                            landThud = audio.CloseHeavy;
                            Plugin.LogInfo($"[Elevator] motor loop from {audio.name}: {motorClip.name}");
                            return motorClip;
                        }
                    }
                }
            }
            return null;
        }

        private static GameObject? FindAsset(string childName)
        {
            RunManager rm = RunManager.instance;
            if (rm == null || rm.levels == null)
                return null;
            foreach (Level level in rm.levels)
            {
                foreach (List<PrefabRef> list in new[] { level.ModulesPassage1, level.ModulesPassage2, level.ModulesPassage3 })
                {
                    if (list == null)
                        continue;
                    foreach (PrefabRef pr in list)
                    {
                        if (pr == null || !pr.IsValid() || (pr.PrefabName != SourceModule && !(pr.ResourcePath ?? "").EndsWith(SourceModule)))
                            continue;
                        GameObject? prefab = pr.Prefab;
                        if (prefab == null)
                            return null;
                        foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                        {
                            if (t.name == childName)
                                return t.gameObject;
                        }
                        return null;
                    }
                }
            }
            return null;
        }

        // Host: start a trip if the car is idle and the dock is real.
        public static bool TryDepart(int dock)
        {
            if (car == null || Moving || dock < 0 || dock >= docks.Count || dock == atDock)
                return false;
            UpstairsNet.SendRide(atDock, dock);
            return true;
        }

        // Every client: the trip as the host called it. The car is snapped to the start dock
        // first so a client that missed an earlier trip still lines up.
        public static void PlayRide(int from, int to)
        {
            if (car == null || to < 0 || to >= docks.Count || from < 0 || from >= docks.Count)
                return;
            Transform root = car.transform.parent;
            if (!Moving)
            {
                root.position = docks[from].Position + Vector3.up * FloorOffset;
                if (body != null) body.position = root.position;
            }
            travelFrom = root.position;
            travelTo = docks[to].Position + Vector3.up * FloorOffset;
            travelLength = Vector3.Distance(travelFrom, travelTo);
            travelT = 0f;
            doorWait = DoorTime;
            doorsHeld = true;
            toDock = to;
            atDock = -1;
            lastCarPos = root.position;
            localRiding = LocalInside();
            Play(sounds?.soundActivate1, root.position);
            EnemyDirector.instance?.SetInvestigate(root.position, 20f);
            Plugin.LogInfo($"[Elevator] {docks[from].Name} -> {docks[to].Name}, {travelLength:0} m, rider={localRiding}");
        }

        public static void Tick(float dt)
        {
            // The game's own loop player: fades in on the way, out on arrival, low-passed
            // through walls like every other loop it runs.
            if (humLoop != null && hum != null && hum.gameObject.activeInHierarchy)
                humLoop.PlayLoop(Moving, 2f, 3f);
            if (car == null)
                return;
            Transform root = car.transform.parent;

            if (!Moving)
                localRiding = false;

            if (hatch != null)
            {
                SwingHatch();
                float h = hatchHeld ? hatchOpen : Mathf.MoveTowards(hatchOpen, hatchWantOpen ? 1f : 0f, dt * 2.5f);
                if (h != hatchOpen || hatchHeld)
                {
                    hatchOpen = h;
                    hatch.localRotation = hatchClosed * Quaternion.Euler(-HatchSwing * h, 0f, 0f);
                }
            }

            if (landWait > 0f)
            {
                landWait -= dt;
                if (landWait <= 0f)
                    doorsHeld = false;
            }
            float target = doorsHeld ? 0f : (AnyoneNear(root.position) ? 1f : 0f);
            float open = Mathf.MoveTowards(doorOpen, target, dt * 2f);
            if (open != doorOpen)
            {
                if ((doorOpen == 0f && open > 0f) || (doorOpen == 1f && open < 1f))
                    Play(sounds?.soundActivate2, root.position);
                doorOpen = open;
                if (doorL != null) doorL.localPosition = closedL + Vector3.left * (DoorSlide * open);
                if (doorR != null) doorR.localPosition = closedR + Vector3.right * (DoorSlide * open);
            }

            // Landing doors follow the car's doors, but only at the dock the car is standing in.
            for (int i = 0; i < docks.Count; i++)
            {
                Dock dock = docks[i];
                if (dock.LandingL == null && dock.LandingR == null)
                    continue;
                float want = i == atDock ? doorOpen : 0f;
                float now = Mathf.MoveTowards(dock.LandingOpen, want, dt * 2f);
                if (now == dock.LandingOpen)
                    continue;
                dock.LandingOpen = now;
                if (dock.LandingL != null) dock.LandingL.localPosition = dock.LandingClosedL + Vector3.left * (DoorSlide * now);
                if (dock.LandingR != null) dock.LandingR.localPosition = dock.LandingClosedR + Vector3.right * (DoorSlide * now);
            }
        }

        // Physics step: move the car, and move the rider with it. Feet stay on the car floor unless
        // they are on their way up from a jump.
        private static void Step(float dt)
        {
            if (car == null || body == null || !Moving)
                return;
            Vector3 before = body.position;
            if (doorWait > 0f)
            {
                // Doors still closing: whoever steps in now rides too.
                doorWait -= dt;
                localRiding = localRiding || LocalInside();
                CarVelocityY = 0f;
                return;
            }
            // Accelerate, cruise, brake, never harder than a body in free fall, so a rider the carry
            // misses still rides the floor down instead of being left in the air.
            travelT += dt;
            float covered = Trapezoid(travelT, travelLength, out bool arrived);
            Vector3 next = Vector3.Lerp(travelFrom, travelTo, travelLength > 0.01f ? covered / travelLength : 1f);
            if (arrived)
                travelT = float.MaxValue;
            body.MovePosition(next);
            Vector3 delta = next - before;
            CarVelocityY = arrived ? 0f : delta.y / dt;
            CarryCargo();
            lastCarPos = next;
            if (arrived)
            {
                atDock = toDock;
                toDock = -1;
                // The car settles with a thud and a shudder, and the doors wait a beat.
                landWait = LandPause;
                Play(landThud, next);
                Play(sounds?.soundActivate3, next);
                SemiFunc.CameraShakeImpactDistance(next, 3f, 0.2f, 4f, 18f);
                // The whole building hears an elevator land.
                EnemyDirector.instance?.SetInvestigate(next, 20f);
            }
        }

        private static float Trapezoid(float t, float length, out bool arrived)
        {
            float rampDist = Speed * Speed / (2f * Accel);
            float peak = length >= 2f * rampDist ? Speed : Mathf.Sqrt(length * Accel);
            float ramp = peak / Accel;
            float cruise = Mathf.Max(0f, (length - 2f * (0.5f * Accel * ramp * ramp)) / peak);
            float total = 2f * ramp + cruise;
            arrived = t >= total;
            if (arrived)
                return length;
            if (t < ramp)
                return 0.5f * Accel * t * t;
            if (t < ramp + cruise)
                return 0.5f * Accel * ramp * ramp + peak * (t - ramp);
            float tb = t - ramp - cruise;
            return 0.5f * Accel * ramp * ramp + peak * cruise + peak * tb - 0.5f * Accel * tb * tb;
        }

        private static bool AnyoneNear(Vector3 carPos)
        {
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                if (!player || player.isDisabled)
                    continue;
                Vector3 d = player.transform.position - carPos;
                if (Mathf.Abs(d.y + FloorOffset) < 3f && new Vector2(d.x, d.z).magnitude < ApproachRange)
                    return true;
            }
            return false;
        }

        // Inside the car footprint and standing on its floor.
        public static bool IsInside(Vector3 pos)
        {
            if (car == null)
                return false;
            Vector3 local = car.transform.parent.InverseTransformPoint(pos);
            return Mathf.Abs(local.x) < cageHalfWidth && Mathf.Abs(local.z) < cageHalfWidth && local.y > -FloorOffset - 0.6f && local.y < 2.5f;
        }

        private static bool LocalInside()
        {
            PlayerAvatar me = PlayerAvatar.instance;
            return me != null && IsInside(me.transform.position);
        }

        // The car's vertical speed this physics step; zero while parked or while the doors work.
        internal static float CarVelocityY { get; private set; }
        internal static float FloorY => body != null ? body.position.y - FloorOffset : 0f;

        private const float RideBand = 0.7f;

        // The controller turns gravity off while it counts you grounded, so on the way down you
        // would hang above a floor that keeps leaving, and on the way up the floor would shove
        // you a step at a time. Runs after the controller's own physics step: anyone standing
        // in the moving car simply travels at the car's speed, and a real jump is left alone.
        internal static void CarryLocal(PlayerController pc)
        {
            if (pc == null || pc.rb == null || pc.rb.isKinematic || !Moving || doorWait > 0f)
                return;
            if (!IsInside(pc.transform.position))
                return;
            Vector3 vel = pc.rb.velocity;
            if (!Standing(pc.rb.position.y, vel.y))
                return;
            vel.y = CarVelocityY;
            pc.rb.velocity = vel;
        }

        private static bool Standing(float y, float velY)
        {
            float above = y - FloorY;
            bool jumping = velY > CarVelocityY + 0.5f;
            return above < RideBand && above > -1f && !jumping;
        }

        // Carts, valuables and anything else loose in the car ride the same way.
        private static readonly Collider[] cargo = new Collider[64];

        private static void CarryCargo()
        {
            if (car == null || body == null)
                return;
            Transform frame = car.transform.parent;
            Vector3 centre = frame.TransformPoint(new Vector3(0f, 1f - FloorOffset + 1.2f, 0f));
            Vector3 half = new Vector3(cageHalfWidth, 1.8f, cageHalfWidth);
            int n = Physics.OverlapBoxNonAlloc(centre, half, cargo, frame.rotation, ~0, QueryTriggerInteraction.Ignore);
            PlayerController pc = PlayerController.instance;
            for (int i = 0; i < n; i++)
            {
                Rigidbody rb = cargo[i].attachedRigidbody;
                if (rb == null || rb.isKinematic || rb == body || (pc != null && rb == pc.rb))
                    continue;
                Vector3 vel = rb.velocity;
                if (!Standing(rb.position.y, vel.y))
                    continue;
                vel.y = CarVelocityY;
                rb.velocity = vel;
            }
        }

        // The local player's grab key on a button they are looking at.
        public static void CheckButtons()
        {
            if (car == null || !SemiFunc.InputDown(InputKey.Grab) && !SemiFunc.InputDown(InputKey.Interact))
                return;
            Camera cam = Camera.main;
            if (cam == null || !Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 2.6f))
                return;
            LiftButton button = hit.collider.GetComponentInParent<LiftButton>();
            // A grabbable hatch swings under the beam; only the fixed fallback plate toggles.
            if (button == null || button.Kind != ButtonKind.Hatch || hatchRef != null)
                return;
            hatchWantOpen = !hatchWantOpen;
            Play(sounds?.soundActivate2, hit.point);
        }

        // The local player holding the hatch drags it: the plate turns on its hinge to point at
        // wherever the beam is pulling, and everyone else is told where it is. Letting go leaves
        // it to fall shut or swing the rest of the way open, whichever is nearer.
        private static void SwingHatch()
        {
            if (hatch == null || hatchRef == null)
                return;
            PlayerAvatar me = PlayerAvatar.instance;
            PhysGrabber? grabber = me != null ? me.physGrabber : null;
            bool held = grabber != null && grabber.grabbed && grabber.grabbedStaticGrabObject != null && grabber.grabbedStaticGrabObject.transform == hatch;
            if (held)
            {
                Transform pivot = hatch.parent;
                Vector3 axis = pivot.TransformDirection(Vector3.right);
                Vector3 closed = pivot.TransformDirection(Vector3.forward);
                Vector3 pull = grabber!.physGrabPointPullerPosition - hatch.position;
                pull -= axis * Vector3.Dot(pull, axis);
                if (pull.sqrMagnitude > 0.01f)
                {
                    float angle = Vector3.SignedAngle(closed, pull, axis);
                    float target = Mathf.Clamp(-angle / HatchSwing, 0f, 1f);
                    hatchOpen = Mathf.MoveTowards(hatchOpen, target, Time.deltaTime * 4f);
                }
                if (!hatchHeld)
                    Play(sounds?.soundActivate2, hatch.position);
                hatchHeld = true;
                if (Time.time - hatchSent >= HatchSendRate)
                {
                    hatchSent = Time.time;
                    UpstairsNet.SendHatch(hatchOpen, settle: false);
                }
            }
            else if (hatchHeld)
            {
                hatchHeld = false;
                hatchWantOpen = hatchOpen > 0.45f;
                UpstairsNet.SendHatch(hatchWantOpen ? 1f : 0f, settle: true);
            }
        }

        // Another player's hand on the hatch.
        public static void RemoteHatch(float open, bool settle)
        {
            if (hatchHeld)
                return;
            if (settle)
                hatchWantOpen = open > 0.5f;
            else
            {
                hatchOpen = Mathf.Clamp01(open);
                hatchWantOpen = hatchOpen > 0.45f;
                if (hatch != null)
                    hatch.localRotation = hatchClosed * Quaternion.Euler(-HatchSwing * hatchOpen, 0f, 0f);
            }
        }

        private static int Neighbour(bool above)
        {
            if (car == null)
                return -1;
            float y = car.transform.parent.position.y - FloorOffset;
            int best = -1;
            float bestGap = float.MaxValue;
            for (int i = 0; i < docks.Count; i++)
            {
                float gap = above ? docks[i].Position.y - y : y - docks[i].Position.y;
                if (gap > 1f && gap < bestGap)
                {
                    bestGap = gap;
                    best = i;
                }
            }
            return best;
        }

        // Which button the local player is looking at right now, for the HUD prompt.
        public static string LookingAt()
        {
            if (car == null)
                return "";
            Camera cam = Camera.main;
            if (cam == null || !Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, 2.6f))
                return "";
            LiftButton button = hit.collider.GetComponentInParent<LiftButton>();
            if (button == null)
                return "";
            return button.Kind switch
            {
                ButtonKind.Up => "up",
                ButtonKind.Down => "down",
                ButtonKind.Hatch => hatchRef != null ? "swing the hatch" : hatchWantOpen ? "close the hatch" : "open the hatch",
                _ => "call"
            };
        }

        internal static List<(Vector3 pos, float yaw, string name)> DockList()
        {
            var list = new List<(Vector3, float, string)>();
            foreach (Dock d in docks)
                list.Add((d.Position, d.Yaw, d.Name));
            return list;
        }

        internal static string HumStatus() => hum == null ? "no hum" : $"playing={hum.isPlaying} vol={hum.volume:0.00} clip={(hum.clip != null ? hum.clip.name : "none")} group={(hum.outputAudioMixerGroup != null ? hum.outputAudioMixerGroup.name : "none")} active={hum.gameObject.activeInHierarchy}";

        internal static LiftButton? FindButton(ButtonKind kind)
        {
            foreach (LiftButton b in Object.FindObjectsOfType<LiftButton>())
                if (b.Kind == kind && b.Attached) return b;
            return null;
        }

        public static string LocalStatus()
        {
            if (car == null)
                return "";
            if (Moving)
                return LocalInside() ? "riding" : "moving";
            return LocalInside() ? "inside" : "";
        }

        private static void Play(Sound? sound, Vector3 at)
        {
            if (sound != null)
                sound.Play(at);
        }
    }
}
