using System.Collections.Generic;
using Photon.Pun;
using REPOLib.Modules;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using Elevators.UI;
using Elevators.Configuration;

namespace Elevators.Services
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
            Hatch,
            Open
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
            // One press per hold: the grab area fires its start event again while the beam
            // stays on, and every re-fire was another click and another squash.
            public bool Held;
            public float HeldSince;
            public Vector3 RestCap;
            public bool RestKnown;
            public bool Squashing;

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
                {
                    grabArea.grabAreaEventOnStart.AddListener(() => PressedOnHost(this, area.GetLatestGrabber()));
                    grabArea.grabAreaEventOnRelease.AddListener(() => Released(this));
                }
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
        private const string ButtonPrefabId = "ElevatorsButton";
        private const string ButtonMeshName = "Museum Elevator Button";
        private const string ShopModulePath = "level/shop/modules/Module - Shop - N - Center Extract";
        private const string ShopButtonName = "Shop Button";
        private const float ShopButtonScale = 0.55f;
        private static GameObject? hatchTemplate;
        private static PrefabRef? hatchRef;
        private const string HatchPrefabId = "ElevatorsHatch";
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
            // Somewhere on the floor by the doors that is definitely walkable.
            public Vector3 Landing;
            public Sound? Speaker;
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
        private static float openHold;
        private const float OpenHoldTime = 6f;
        // A press made mid-trip: the car carries on to that dock after it lands, doors shut.
        private static int queued = -1;
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
                    grab.hoverText = button.Kind switch { ButtonKind.Up => "Up", ButtonKind.Down => "Down", ButtonKind.Hatch => "Roof hatch", ButtonKind.Open => "Open the doors", _ => "Call the elevator" };
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
            if (!TakeHold(button))
                return;
            Plugin.LogInfo($"[Elevator] grabbed {button.Kind} (dock {button.Dock})");
            if (SemiFunc.IsMasterClientOrSingleplayer())
                HandlePress(button, player.playerAvatar);
            else
            {
                // Instant feedback; the host's grab area is the press that counts.
                Press(button);
                RideFromClient(button);
            }
        }

        // The host's grab area saw a beam take hold: the press that counts, plus the click for
        // everyone who did not press it themselves.
        private static void PressedOnHost(LiftButton button, PlayerAvatar? grabber)
        {
            if (button == null || !SemiFunc.IsMasterClientOrSingleplayer())
                return;
            if (!TakeHold(button))
                return;
            Plugin.LogInfo($"[Elevator] grab area press {button.Kind} (dock {button.Dock})");
            HandlePress(button, grabber);
        }

        // The first take of a hold counts; the rest of the hold is ignored until it is let go.
        // A hold nobody is on any more (the release was missed) times out on its own.
        private static bool TakeHold(LiftButton button)
        {
            if (button.Held)
            {
                StaticGrabObject grab = button.GetComponent<StaticGrabObject>();
                bool nobody = grab == null || (!grab.grabbed && grab.playerGrabbing.Count == 0);
                if (!nobody || Time.time - button.HeldSince < 1f)
                    return false;
            }
            button.Held = true;
            button.HeldSince = Time.time;
            return true;
        }

        public static void Released(LiftButton button)
        {
            if (button != null)
                button.Held = false;
        }

        public static void Released(StaticGrabObject grab)
        {
            LiftButton button = grab != null ? grab.GetComponent<LiftButton>() : null;
            if (button != null)
                button.Held = false;
        }

        // The one press on the host, whether it came from the host's own grab or the grab area,
        // with the two never counting twice. Offline the buttons carry no network id, so the host
        // presses its own copy here and only the other clients are told which one, and whether
        // it was refused.
        private static void HandlePress(LiftButton button, PlayerAvatar? grabber)
        {
            if (Time.time - button.LastPress < 0.3f)
                return;
            int grabberView = grabber != null && grabber.photonView != null ? grabber.photonView.ViewID : -1;
            bool denied = !Ride(button);
            Press(button, denied);
            if (SemiFunc.IsMultiplayer())
                ElevatorsNet.SendPress(button.GetComponent<PhotonView>()?.ViewID ?? -1, grabberView, denied);
        }

        // Everyone: the click and the squash. The presser's own client already played it on the
        // grab, and the short dedupe in Press keeps that from doubling.
        public static void PlayPress(int buttonView, int grabberView, bool denied)
        {
            PhotonView view = PhotonView.Find(buttonView);
            LiftButton button = view != null ? view.GetComponent<LiftButton>() : null;
            if (button == null)
                return;
            Press(button, denied);
        }

        private static Sound? standPress;
        private static Sound? standDeny;
        private static bool standPressLooked;

        private static void Press(LiftButton button, bool denied = false)
        {
            if (Time.time - button.LastPress < 0.3f)
                return;
            button.LastPress = Time.time;
            // The upgrade stand's press has the weight the extraction click lacks, and its
            // refusal is the game's own "no".
            if (!standPressLooked)
            {
                standPressLooked = true;
                GameObject? stands = Resources.Load<GameObject>("level/shop/modules/Module - Shop - N - Corner Stands");
                UpgradeStand? stand = stands != null ? stands.GetComponentInChildren<UpgradeStand>(true) : null;
                standPress = stand != null ? stand.soundButtonPress : null;
                standDeny = stand != null ? stand.soundButtonDeny : null;
            }
            if (denied)
            {
                Play(standDeny ?? sounds?.soundButton, button.transform.position);
                Plugin.Instance.StartCoroutine(Squash(button.transform));
                return;
            }
            Play(sounds?.soundButton, button.transform.position);
            Play(standPress, button.transform.position);
            Plugin.Instance.StartCoroutine(Squash(button.transform));
        }

        // What a press does, on the host. Call at the dock the car stands on opens the doors. A
        // press mid-trip queues the next dock that way, so two presses of up is two floors up.
        // Anything the car cannot do, the top floor's up, is refused.
        private static bool Ride(LiftButton button)
        {
            if (car == null)
                return false;
            if (button.Kind == ButtonKind.Open)
            {
                if (Moving)
                    return false;
                ElevatorsNet.SendDoors(OpenHoldTime);
                return true;
            }
            if (Moving)
            {
                int next = button.Kind switch
                {
                    ButtonKind.Call => button.Dock,
                    ButtonKind.Up => NeighbourOf(queued >= 0 ? queued : toDock, above: true),
                    ButtonKind.Down => NeighbourOf(queued >= 0 ? queued : toDock, above: false),
                    _ => -1
                };
                if (next < 0 || next == toDock || next == queued)
                    return false;
                // Further along the way the car is already going: the trip simply runs on to
                // there, no stop between. Anything else waits its turn.
                bool goingUp = travelTo.y > travelFrom.y;
                bool beyond = goingUp ? docks[next].Position.y > docks[toDock].Position.y : docks[next].Position.y < docks[toDock].Position.y;
                if (beyond && queued < 0 && Extend(next))
                    return true;
                queued = next;
                return true;
            }
            int target = button.Kind switch
            {
                ButtonKind.Call => button.Dock,
                ButtonKind.Up => Neighbour(above: true),
                ButtonKind.Down => Neighbour(above: false),
                _ => -1
            };
            if (target < 0)
                return false;
            if (target == atDock)
            {
                if (button.Kind != ButtonKind.Call)
                    return false;
                ElevatorsNet.SendDoors(OpenHoldTime);
                return true;
            }
            return TryDepart(target);
        }

        // A client's press goes to the host as a plain ride request; the host's grab area
        // normally beats it there and the dedupe drops the second.
        private static void RideFromClient(LiftButton button)
        {
            int target = button.Kind switch
            {
                ButtonKind.Call => button.Dock,
                ButtonKind.Up => Neighbour(above: true),
                ButtonKind.Down => Neighbour(above: false),
                _ => -1
            };
            if (target >= 0 && target != atDock)
                ElevatorsNet.RequestRide(target);
        }

        private static System.Collections.IEnumerator Squash(Transform plate)
        {
            LiftButton button = plate.GetComponent<LiftButton>();
            if (button != null && button.Squashing)
                yield break;
            if (button != null)
                button.Squashing = true;
            try
            {
                yield return SquashOnce(plate, button);
            }
            finally
            {
                if (button != null)
                    button.Squashing = false;
            }
        }

        private static System.Collections.IEnumerator SquashOnce(Transform plate, LiftButton? button)
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
            if (button != null && !button.RestKnown)
            {
                button.RestCap = cap.localPosition;
                button.RestKnown = true;
            }
            Vector3 up = button != null ? button.RestCap : cap.localPosition;
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
                if (g.name == "Elevators dock" || g.name == "Elevators shaft" || g.name == "Elevators shaft dressing")
                    Object.Destroy(g);
            }
            car = null;
            doorL = doorR = null;
            hum = null;
            humLoop = null;
            landWait = 0f;
            openHold = 0f;
            queued = -1;
            pursuers.Clear();
            lastRide.Clear();
            monsterPresses.Clear();
            lostTicks.Clear();
            aboard.Clear();
            foreach (NavMeshObstacle block in dockBlocks)
                if (block != null) Object.Destroy(block.gameObject);
            foreach (NavMeshLink link in doorLinks)
                if (link != null) Object.Destroy(link.gameObject);
            doorLinks.Clear();
            LastPatrolRider = null;
            Arrivals.Clear();
            dockBlocks.Clear();
            hatch = null;
            hatchOpen = 0f;
            hatchWantOpen = false;
            hatchHeld = false;
            docks.Clear();
            shutBoxes.Clear();
            atDock = -1;
            toDock = -1;
            localRiding = false;
            sounds = null;
        }

        public static void Build(IReadOnlyList<(Vector3 pad, float yaw, string name, Vector3 landing)> pads)
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
            foreach ((Vector3 pad, float yaw, string name, Vector3 landing) in pads)
                docks.Add(new Dock { Position = pad, Yaw = yaw, Name = name, Landing = landing });

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
            DressShaft();
            BuildMapDoors();
            BuildSpeakers();
            foreach (LiftButton waiting in loose.ToArray())
                Attach(waiting);
            SpawnButtons();
            if (SemiFunc.IsMasterClientOrSingleplayer())
            {
                Plugin.Instance.StartCoroutine(JoinNavMesh());
                Plugin.Instance.StartCoroutine(Patrols());
                Plugin.Instance.StartCoroutine(RescueMonsters());
            }
            else
                ElevatorsNet.AskCar();
            Plugin.LogInfo($"[Elevator] car at {docks[ground].Name}, {docks.Count} docks");
        }

        private static void BuildCar(Dock dock)
        {
            var holder = new GameObject("Elevators elevator");
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
            // The other wall holds the door button.
            Transform other = Panel(body.transform, new Vector3(-(wallX - 0.02f), -0.75f, -0.6f), Quaternion.LookRotation(Vector3.right), 0.44f);
            AddSlot(other, new Vector3(0f, 0.08f, 0.02f), ButtonKind.Open, -1, "OPEN");
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
            // Drawn the way the player is drawn between physics steps, or at speed the floor
            // renders a step apart from the feet standing on it.
            rb.interpolation = PlayerController.instance != null && PlayerController.instance.rb != null ? PlayerController.instance.rb.interpolation : RigidbodyInterpolation.Interpolate;
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
            if (doorL != null) SoftDoor(doorL);
            if (doorR != null) SoftDoor(doorR);
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
                var landing = new GameObject("Elevators dock");
                landing.transform.SetPositionAndRotation(dock.Position + Vector3.up * FloorOffset, Quaternion.Euler(0f, dock.Yaw, 0f));
                dock.LandingL = CopyDoor(carriage, "Museum Elevator door (1)", landing.transform);
                dock.LandingR = CopyDoor(carriage, "Museum Elevator door", landing.transform);
                // Recessed inside the wall, so an open door slides behind the frame and not across
                // the wall plate and its button.
                if (dock.LandingL != null) { dock.LandingClosedL = ClosedAt(dock.LandingL, left: true, recess: true); dock.LandingL.localPosition = dock.LandingClosedL; }
                if (dock.LandingR != null) { dock.LandingClosedR = ClosedAt(dock.LandingR, left: false, recess: true); dock.LandingR.localPosition = dock.LandingClosedR; }
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
            var landing = new GameObject("Elevators dock");
            landing.transform.SetPositionAndRotation(dock.Position + Vector3.up * FloorOffset, Quaternion.Euler(0f, dock.Yaw, 0f));
            dock.LandingL = CopyDoor(carriageAsset.transform, "Museum Elevator door (1)", landing.transform);
            dock.LandingR = CopyDoor(carriageAsset.transform, "Museum Elevator door", landing.transform);
            if (dock.LandingL != null) { dock.LandingClosedL = ClosedAt(dock.LandingL, left: true, recess: true); dock.LandingL.localPosition = dock.LandingClosedL; }
            if (dock.LandingR != null) { dock.LandingClosedR = ClosedAt(dock.LandingR, left: false, recess: true); dock.LandingR.localPosition = dock.LandingClosedR; }
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
            SoftDoor(copy.transform);
            return copy.transform;
        }

        // A sliding door at two metres a second was sweeping through loot left at the threshold
        // and chipping it. The door's own colliders now stop only players, and a second box that
        // stops everything comes on once the door is fully shut, so nothing rolls into the shaft.
        private static readonly List<(Transform door, Collider shut)> shutBoxes = new List<(Transform, Collider)>();

        private static void SoftDoor(Transform door)
        {
            int playerOnly = LayerMask.NameToLayer("PlayerOnlyCollision");
            MeshFilter filter = door.GetComponent<MeshFilter>();
            if (playerOnly < 0 || filter == null || filter.sharedMesh == null)
                return;
            // The door's own colliders sit on its rendered object, and that layer is not drawn,
            // so the blocking moves to children of its own.
            foreach (Collider c in door.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            Bounds b = filter.sharedMesh.bounds;
            var wall = new GameObject("players");
            wall.transform.SetParent(door, false);
            wall.layer = playerOnly;
            var wallBox = wall.AddComponent<BoxCollider>();
            wallBox.center = b.center;
            wallBox.size = b.size;
            var shut = new GameObject("shut");
            shut.transform.SetParent(door, false);
            shut.layer = 0;
            var box = shut.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;
            // Doors are built shut.
            box.enabled = true;
            shutBoxes.Add((door, box));
        }

        private static bool navBaking;

        private static void ShutBox(Transform? door, bool shut)
        {
            if (door == null || navBaking)
                return;
            foreach ((Transform d, Collider box) in shutBoxes)
            {
                if (d == door && box != null && box.enabled != shut)
                    box.enabled = shut;
            }
        }

        // The prefab parks its doors ajar. Shut means the two inner edges meet in the middle, so the
        // closed spot comes from the door mesh's own width.
        // The room side of the shaft wall, in the car's frame, and how far inside it the landing
        // doors' outer face sits: recessed like a real landing, never proud of the wall.
        private const float WallFace = -1.6f;
        private const float LandingInset = 0.12f;

        private static Vector3 ClosedAt(Transform door, bool left, bool recess = false)
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
            if (recess)
            {
                // Outer face at the inset, whatever way the mesh hangs off its pivot.
                float outer = b.center.z - b.extents.z;
                at.z = WallFace + LandingInset - outer * door.localScale.z;
                Plugin.LogInfo($"[Elevator] landing door {(left ? "L" : "R")} mesh z {b.center.z - b.extents.z:0.00}..{b.center.z + b.extents.z:0.00}, pivot z {at.z:0.00}");
            }
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
                var mount = new GameObject("Elevators dock");
                mount.transform.SetPositionAndRotation(dock.Position + facing * MuseumPlate, facing);
                var face = new GameObject("face").transform;
                face.SetParent(mount.transform, false);
                face.localRotation = Quaternion.Euler(0f, 180f, 0f);
                AddSlot(face, new Vector3(0f, 0f, 0.03f), ButtonKind.Call, index, "CALL");
                return;
            }
            // An unscaled stand holds the post cube and the panel, so the panel keeps its shape.
            var stand = new GameObject("Elevators dock");
            // Clear of the door frame and its pillar, a step into the room.
            stand.transform.SetPositionAndRotation(dock.Position + facing * new Vector3(2.5f, 0f, -2.1f), facing);
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
            // The Museum's own elevator wall plate, in its own brushed material, stretched to fit.
            GameObject? plateAsset = FindAsset(ButtonMeshName);
            MeshFilter? plateMesh = plateAsset != null ? plateAsset.GetComponent<MeshFilter>() : null;
            GameObject plate;
            if (plateMesh != null && plateMesh.sharedMesh != null)
            {
                plate = Object.Instantiate(plateAsset!, parent);
                StripScripts(plate);
                foreach (Collider c in plate.GetComponentsInChildren<Collider>(true))
                    Object.Destroy(c);
                Bounds b = plateMesh.sharedMesh.bounds;
                // The plate is thin along z with its face toward -z, the room side where the Museum
                // hangs it. Turned about to face the panel's +z, its face flush with the panel plane,
                // so the buttons and their words stand proud of it.
                var pivot = new GameObject("Elevators panel").transform;
                pivot.SetParent(parent, false);
                pivot.localPosition = localPos;
                pivot.localRotation = localRot;
                plate.transform.SetParent(pivot, false);
                plate.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                float sy = height / Mathf.Max(0.01f, b.size.y);
                plate.transform.localScale = new Vector3(0.28f / Mathf.Max(0.01f, b.size.x), sy, 1f);
                float front = -(b.center.z - b.extents.z);
                plate.transform.localPosition = new Vector3(0f, -b.center.y * sy, -front);
            }
            else
            {
                plateMaterial ??= new Material(Shader.Find("Sprites/Default")) { color = new Color(0.16f, 0.16f, 0.18f) };
                plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
                plate.name = "Elevators panel";
                Object.Destroy(plate.GetComponent<Collider>());
                plate.transform.SetParent(parent, false);
                plate.transform.localPosition = localPos;
                plate.transform.localRotation = localRot;
                plate.transform.localScale = new Vector3(0.28f, height, 0.03f);
                plate.GetComponent<Renderer>().sharedMaterial = plateMaterial;
            }
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
            var shaft = new GameObject("Elevators shaft");
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

        // The Museum's own shaft walls and pipes, stacked the length of the shaft, so a look
        // through a crack shows brick and pipework and a fall out of the car has walls to hit.
        // The shaft group in the module spans forty metres around its floor; copies go up in
        // forty metre steps. Front tiles at a dock give way to the doorway, and wherever a Museum
        // room already has its own shaft, nothing is doubled.
        private static void DressShaft()
        {
            GameObject? group = FindAsset("Elevator");
            Transform? walls = group != null ? group.transform.Find("Walls") : null;
            Transform? pipes = group != null ? group.transform.Find("Pipes") : null;
            if (walls == null && pipes == null)
                return;
            var sorted = new List<Dock>(docks);
            sorted.Sort((a, b) => a.Position.y.CompareTo(b.Position.y));
            Dock low = sorted[0];
            Dock high = sorted[sorted.Count - 1];
            float bottom = low.Position.y - ShaftBelow;
            float top = high.Position.y + 4.8f;
            Quaternion facing = Quaternion.Euler(0f, low.Yaw, 0f);
            var holder = new GameObject("Elevators shaft dressing").transform;
            int tiles = 0;
            // Copies stand on the lowest dock's floor, so every tile boundary lands on a dock and
            // the tile at a doorway is a whole one, not one straddling it.
            for (float y0 = low.Position.y; y0 - 20f < top; y0 += 40f)
            {
                // One frame per copy standing where the module's own shaft group would, with each
                // part kept at its own offset inside it (the pipes sit well off the group origin).
                var frame = new GameObject("shaft frame").transform;
                frame.SetParent(holder, false);
                frame.SetPositionAndRotation(new Vector3(low.Position.x, y0, low.Position.z), facing);
                foreach (Transform? part in new[] { walls, pipes })
                {
                    if (part == null)
                        continue;
                    GameObject copy = Object.Instantiate(part.gameObject, frame);
                    StripScripts(copy);
                    copy.transform.localPosition = part.localPosition;
                    copy.transform.localRotation = part.localRotation;
                    copy.transform.localScale = part.localScale;
                    // Tiles sit directly under the group and under its floor groups; each one is
                    // judged by where it stands in the shaft's frame.
                    var pieces = new List<Transform>();
                    foreach (Transform child in copy.transform)
                    {
                        if (child.GetComponentInChildren<Renderer>(true) == null)
                            continue;
                        if (child.name.StartsWith("FLOOR"))
                            foreach (Transform grand in child) pieces.Add(grand);
                        else
                            pieces.Add(child);
                    }
                    foreach (Transform piece in pieces)
                    {
                        float y = piece.position.y;
                        Vector3 inShaft = frame.InverseTransformPoint(piece.position);
                        bool keep = y >= bottom - 0.5f && y < top;
                        if (keep && inShaft.z < -1f)
                        {
                            // The front row: open at every dock, and its own doorway pieces never.
                            // A tile stands on its origin and reaches four metres up.
                            if (piece.name.Contains("Door"))
                                keep = false;
                            foreach (Dock d in docks)
                                if (y + 4f > d.Position.y + 0.1f && y < d.Position.y + 3.4f) keep = false;
                        }
                        if (keep)
                        {
                            foreach (Dock d in docks)
                                if (d.InShaft && y >= d.Position.y - 20.5f && y < d.Position.y + 16.5f) keep = false;
                        }
                        piece.gameObject.SetActive(keep);
                        if (keep) tiles++;
                    }
                }
            }
            // Dim service lights up the back wall.
            for (float y = bottom + 6f; y < top; y += 12f)
            {
                var lamp = new GameObject("shaft light").AddComponent<Light>();
                lamp.transform.SetParent(holder, false);
                lamp.transform.position = new Vector3(low.Position.x, y, low.Position.z) + facing * new Vector3(2.1f, 0f, 3.1f);
                lamp.type = LightType.Point;
                lamp.color = new Color(1f, 0.82f, 0.6f);
                lamp.intensity = 0.7f;
                lamp.range = 7f;
                lamp.shadows = LightShadows.None;
            }
            Plugin.LogInfo($"[Elevator] shaft dressed with {tiles} Museum pieces");
        }

        // The map marks the elevator doorway at every dock the way it marks a door, in its own
        // colour.
        private static readonly Color MapDoorTint = new Color(1f, 0.62f, 0.2f);

        private static void BuildMapDoors()
        {
            Map map = Map.Instance;
            if (map == null || map.Door1x1Object == null)
                return;
            foreach (Dock dock in docks)
            {
                MapLayer layer = map.GetLayerParent(dock.Position.y);
                if (layer == null)
                    continue;
                Quaternion facing = Quaternion.Euler(0f, dock.Yaw, 0f);
                Vector3 at = dock.Position + facing * new Vector3(0f, 0f, -1.5f);
                GameObject go = Object.Instantiate(map.Door1x1Object, layer.transform);
                go.name = "Elevators map door";
                go.transform.position = at * map.Scale + layer.transform.position + map.GetLayerPosition(layer.layer);
                go.transform.rotation = facing;
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.material != null && r.material.HasProperty("_Color"))
                        r.material.color = MapDoorTint;
                }
            }
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
            // A solid wall behind the face, so a flying head that follows the car into the shaft
            // stays in the shaft and a player never leaves it sideways.
            var solid = new GameObject("shaft wall");
            solid.transform.SetParent(shaft, false);
            solid.transform.localPosition = turn * new Vector3(0f, 0f, 2.75f) + Vector3.up * ((from + to) * 0.5f - mid);
            solid.transform.localRotation = turn;
            var box = solid.AddComponent<BoxCollider>();
            box.size = new Vector3(5.6f, to - from, 0.3f);
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
            copy.name = "Elevators shaft pit";
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
            // The Museum's car carves itself out of the navmesh so nothing paths into a parked
            // elevator; this one wants monsters in it, so those go too.
            int carved = 0;
            foreach (NavMeshObstacle obstacle in root.GetComponentsInChildren<NavMeshObstacle>(true))
            {
                Object.DestroyImmediate(obstacle);
                carved++;
            }
            if (carved > 0)
                Plugin.LogInfo($"[Elevator] {root.name}: {carved} navmesh obstacle(s) stripped");
        }

        // The shaft carries sound. A speaker by every dock's doors plays the motor while the car
        // moves, so a player on any floor hears it coming, going, and stopping.
        private static void BuildSpeakers()
        {
            if (hum == null || hum.clip == null)
                return;
            foreach (Dock dock in docks)
            {
                var go = new GameObject("Elevators shaft speaker");
                go.transform.position = dock.Position + Vector3.up * 1.6f + Quaternion.Euler(0f, dock.Yaw, 0f) * new Vector3(0f, 0f, -0.6f);
                var src = go.AddComponent<AudioSource>();
                src.clip = hum.clip;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = hum.rolloffMode;
                src.minDistance = 2f;
                src.maxDistance = 14f;
                src.volume = 0f;
                src.outputAudioMixerGroup = hum.outputAudioMixerGroup;
                dock.Speaker = new Sound
                {
                    Sounds = new[] { hum.clip },
                    Source = src,
                    Volume = 0.3f,
                    VolumeRandom = 0f,
                    Pitch = 0.9f,
                    PitchRandom = 0f,
                    SpatialBlend = 1f,
                    Doppler = 0f,
                    ReverbMix = 1f,
                    FalloffMultiplier = 1f
                };
            }
        }

        // The depart and landing sounds, heard through every landing's doors, quieter.
        private static void PlayAtDocks(Sound? sound, Vector3 except, float volume)
        {
            if (sound == null)
                return;
            foreach (Dock dock in docks)
            {
                if (Vector3.Distance(dock.Position, except) < 2f)
                    continue;
                sound.Play(dock.Position + Vector3.up * 1.5f, volume);
            }
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
            NotePursuers();
            NoteAboard();
            ElevatorsNet.SendRide(atDock, dock);
            return true;
        }

        // Monsters that had a rider in their sights when the car left. The ride is no escape
        // from them: a while after the car lands they turn up on that floor, out of sight, and
        // come looking, the way the game already moves a monster that has lost you.
        private static readonly List<EnemyParent> pursuers = new List<EnemyParent>();
        private const float PursuitRange = 30f;

        private static void NotePursuers()
        {
            if (Moving)
                return;
            pursuers.Clear();
            if (!PluginConfig.EnemiesOnNewFloors.Value || car == null || EnemyDirector.instance == null)
                return;
            Vector3 at = car.transform.parent.position;
            foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null)
                    continue;
                Enemy enemy = parent.Enemy;
                PlayerAvatar target = enemy.TargetPlayerAvatar;
                bool onRider = target != null && !target.isDisabled && IsInside(target.transform.position);
                bool chasing = enemy.CheckChase() || enemy.CurrentState == EnemyState.ChaseBegin || enemy.CurrentState == EnemyState.Chase;
                if (!chasing)
                    continue;
                // Most kinds keep their target to themselves; one in a chase right at the car as
                // it leaves with someone inside was chasing that someone.
                float reach = onRider ? PursuitRange : 12f;
                if (Vector3.Distance(enemy.transform.position, at) > reach || (!onRider && !AnyoneInside()))
                    continue;
                pursuers.Add(parent);
            }
            if (pursuers.Count > 0)
                Plugin.LogInfo($"[Elevator] {pursuers.Count} monster(s) saw the car leave");
        }

        private static bool AnyoneInside()
        {
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                if (player != null && !player.isDisabled && IsInside(player.transform.position))
                    return true;
            }
            return false;
        }

        private static void SendPursuers(Vector3 landing)
        {
            if (pursuers.Count == 0 || !SemiFunc.IsMasterClientOrSingleplayer())
                return;
            foreach (EnemyParent parent in pursuers)
            {
                // The Shadow Child teleports on its own; it is simply there sooner.
                bool blinks = parent.Enemy != null && parent.Enemy.GetComponentInChildren<EnemyThinMan>(true) != null;
                Plugin.Instance.StartCoroutine(Pursue(parent, landing, blinks ? Random.Range(2f, 5f) : Random.Range(6f, 14f)));
            }
            pursuers.Clear();
        }

        // Every so often an idle monster near a dock takes the elevator to another floor: it
        // walks up as the car is called, stands at the doors as they shut, and steps out where
        // the car lands. The shaft carries every sound of it. The Shadow Child never bothers.
        private static System.Collections.IEnumerator Patrols()
        {
            while (car != null)
            {
                // More floors, more traffic: the base interval is for one extra floor.
                float interval = PluginConfig.MonsterPatrolInterval.Value / Mathf.Max(1, FloorCount() - 1);
                yield return new WaitForSeconds(interval > 0f ? interval * Random.Range(0.7f, 1.3f) : 30f);
                if (car == null)
                    yield break;
                if (interval <= 0f || !PluginConfig.EnemiesOnNewFloors.Value || Moving || EnemyDirector.instance == null)
                    continue;
                if (!PickPatrol(out EnemyParent? rider, out int from, out int to) || rider == null)
                    continue;
                yield return Patrol(rider, from, to);
            }
        }

        private static int FloorCount()
        {
            var heights = new List<float>();
            foreach (Dock d in docks)
            {
                if (!heights.Exists(h => Mathf.Abs(h - d.Position.y) < 6f))
                    heights.Add(d.Position.y);
            }
            return heights.Count;
        }

        // When each monster last rode, so the same one does not live in the elevator.
        private static readonly Dictionary<EnemyParent, float> lastRide = new Dictionary<EnemyParent, float>();

        private static bool PickPatrol(out EnemyParent? rider, out int from, out int to)
        {
            rider = null;
            from = -1;
            to = -1;
            float rest = 2f * PluginConfig.MonsterPatrolInterval.Value;
            var candidates = new List<(EnemyParent parent, int dock)>();
            foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null)
                    continue;
                Enemy enemy = parent.Enemy;
                if (enemy.CheckChase() || enemy.GetComponentInChildren<EnemyThinMan>(true) != null)
                    continue;
                if (lastRide.TryGetValue(parent, out float when) && Time.time - when < rest)
                    continue;
                if (PlayerWithin(enemy.transform.position, 14f))
                    continue;
                int dock = NearestDock(enemy.transform.position, 25f);
                if (dock >= 0)
                    candidates.Add((parent, dock));
            }
            if (candidates.Count == 0)
                return false;
            (rider, from) = candidates[Random.Range(0, candidates.Count)];
            // Somewhere else, leaning toward a floor with someone on it.
            var elsewhere = new List<int>();
            var withPlayers = new List<int>();
            for (int i = 0; i < docks.Count; i++)
            {
                if (Mathf.Abs(docks[i].Position.y - docks[from].Position.y) < 6f)
                    continue;
                elsewhere.Add(i);
                if (PlayerWithin(docks[i].Position, 60f, sameFloor: true))
                    withPlayers.Add(i);
            }
            if (elsewhere.Count == 0)
                return false;
            List<int> pool = withPlayers.Count > 0 && Random.value < 0.8f ? withPlayers : elsewhere;
            to = pool[Random.Range(0, pool.Count)];
            return true;
        }

        internal static void StartPatrol(EnemyParent rider, int from, int to) => Plugin.Instance.StartCoroutine(Patrol(rider, from, to));

        // One patrol at a time: two riders calling the car against each other is a car that
        // never leaves.
        private static bool patrolling;
        internal static bool Patrolling => patrolling;

        private static System.Collections.IEnumerator Patrol(EnemyParent rider, int from, int to)
        {
            if (patrolling)
                yield break;
            patrolling = true;
            try
            {
                yield return PatrolRide(rider, from, to);
            }
            finally
            {
                patrolling = false;
            }
        }

        private static System.Collections.IEnumerator PatrolRide(EnemyParent rider, int from, int to)
        {
            Plugin.LogInfo($"[Elevator] {rider.enemyName} heads for the elevator at {docks[from].Name}, bound for {docks[to].Name}");
            // The monster walks to the call button first; nothing happens until it is in reach.
            LiftButton? call = null;
            foreach (LiftButton b in Object.FindObjectsOfType<LiftButton>())
                if (b.Kind == ButtonKind.Call && b.Dock == from && b.Attached) { call = b; break; }
            Vector3 buttonAt = call != null ? call.transform.position : docks[from].Landing;
            if (rider.Enemy.HasStateInvestigate)
                rider.Enemy.StateInvestigate.Set(docks[from].Landing, false);
            float walked = 0f;
            while (walked < 12f)
            {
                yield return new WaitForSeconds(0.5f);
                walked += 0.5f;
                if (rider == null || !rider.Spawned || rider.Enemy == null || rider.Enemy.CheckChase())
                    yield break;
                if (Vector3.Distance(rider.Enemy.transform.position, buttonAt) < 3f)
                    break;
                if (walked % 6f < 0.5f && rider.Enemy.HasStateInvestigate)
                    rider.Enemy.StateInvestigate.Set(docks[from].Landing, false);
            }
            if (rider == null || !rider.Spawned || rider.Enemy == null)
                yield break;
            // The walk is best effort. One that dawdles, or one whose kind never investigates,
            // is brought to the button, as long as nobody on that floor is close enough to see.
            if (Vector3.Distance(rider.Enemy.transform.position, buttonAt) >= 3f)
            {
                if (PlayerWithin(docks[from].Landing, 10f, sameFloor: true))
                    yield break;
                rider.Enemy.EnemyTeleported(docks[from].Landing);
                yield return new WaitForSeconds(0.5f);
            }
            if (Moving)
                yield break;
            // Its press: the click, and the car comes or the doors open.
            if (call != null)
                Press(call);
            Plugin.LogInfo($"[Elevator] {rider.enemyName} pressed the call button at {docks[from].Name}");
            if (atDock != from)
            {
                if (!TryDepart(from))
                    yield break;
                float t = 0f;
                while ((Moving || atDock != from) && t < 40f) { t += 0.5f; yield return new WaitForSeconds(0.5f); }
                if (atDock != from || Moving)
                    yield break;
                // The landing pause opens the doors on its own.
                yield return new WaitForSeconds(LandPause + 0.5f);
            }
            ElevatorsNet.SendDoors(6f);
            if (rider.Enemy.HasStateInvestigate)
                rider.Enemy.StateInvestigate.Set(docks[from].Landing, false);
            yield return new WaitForSeconds(2f);
            if (rider == null || !rider.Spawned || rider.Enemy == null || Moving || atDock != from)
            {
                ElevatorsNet.SendDoors(0f);
                yield break;
            }
            if (rider.Enemy.CheckChase() || LocalInside() || PlayerWithin(car!.transform.parent.position, 3f))
            {
                ElevatorsNet.SendDoors(0f);
                yield break;
            }
            // At the doors as they shut. One that never made the walk is brought the last stretch.
            if (Vector3.Distance(rider.Enemy.transform.position, docks[from].Landing) > 4f)
                rider.Enemy.EnemyTeleported(docks[from].Landing);
            yield return new WaitForSeconds(1f);
            ElevatorsNet.SendDoors(0f);
            if (!TryDepart(to))
                yield break;
            float w = 0f;
            while ((Moving || atDock != to) && w < 40f) { w += 0.5f; yield return new WaitForSeconds(0.5f); }
            if (rider == null || !rider.Spawned || rider.Enemy == null || atDock != to)
                yield break;
            rider.Enemy.EnemyTeleported(docks[to].Landing);
            lastRide[rider] = Time.time;
            LastPatrolRider = rider;
            Arrivals.Add(rider);
            EnemyDirector.instance?.SetInvestigate(docks[to].Landing, 12f);
            Plugin.LogInfo($"[Elevator] {rider.enemyName} stepped out at {docks[to].Name}");
        }

        private static bool PlayerWithin(Vector3 at, float range, bool sameFloor = false)
        {
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                if (player == null || player.isDisabled)
                    continue;
                Vector3 p = player.transform.position;
                if (sameFloor && Mathf.Abs(p.y - at.y) > 6f)
                    continue;
                if (Vector3.Distance(p, at) < range)
                    return true;
            }
            return false;
        }

        private static int NearestDock(Vector3 at, float maxFlat)
        {
            int best = -1;
            float bestFlat = maxFlat;
            for (int i = 0; i < docks.Count; i++)
            {
                if (Mathf.Abs(docks[i].Position.y - at.y) > 6f)
                    continue;
                float flat = Vector2.Distance(new Vector2(at.x, at.z), new Vector2(docks[i].Position.x, docks[i].Position.z));
                if (flat < bestFlat)
                {
                    bestFlat = flat;
                    best = i;
                }
            }
            return best;
        }

        private static System.Collections.IEnumerator Pursue(EnemyParent parent, Vector3 landing, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (parent == null || !parent.Spawned || parent.Enemy == null || LevelGenerator.Instance == null)
                yield break;
            // A path point on the landing's floor, near enough to matter, far enough to arrive unseen.
            var spots = new List<LevelPoint>();
            foreach (LevelPoint point in LevelGenerator.Instance.LevelPathPoints)
            {
                if (point == null || (point.Room != null && point.Room.Truck))
                    continue;
                Vector3 p = point.transform.position;
                if (Mathf.Abs(p.y - landing.y) > 6f)
                    continue;
                float flat = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(landing.x, landing.z));
                if (flat >= 10f && flat <= 30f)
                    spots.Add(point);
            }
            if (spots.Count == 0)
                yield break;
            LevelPoint spot = spots[Random.Range(0, spots.Count)];
            parent.Enemy.EnemyTeleported(spot.transform.position);
            EnemyDirector.instance?.SetInvestigate(landing, 40f);
            Plugin.LogInfo($"[Elevator] {parent.enemyName} followed the car");
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
            // A press inside the landing pause must not let that pause open the doors mid-trip.
            landWait = 0f;
            openHold = 0f;
            doorsHeld = true;
            toDock = to;
            atDock = -1;
            lastCarPos = root.position;
            localRiding = LocalInside();
            Play(sounds?.soundActivate1, root.position);
            PlayAtDocks(sounds?.soundActivate1, root.position, 0.5f);
            HearTheShaft();
            Plugin.LogInfo($"[Elevator] {docks[from].Name} -> {docks[to].Name}, {travelLength:0} m, rider={localRiding}");
        }

        public static void Tick(float dt)
        {
            // The game's own loop player: fades in on the way, out on arrival, low-passed
            // through walls like every other loop it runs.
            if (humLoop != null && hum != null && hum.gameObject.activeInHierarchy)
                humLoop.PlayLoop(Moving, 2f, 3f);
            foreach (Dock dock in docks)
                dock.Speaker?.PlayLoop(Moving, 1.5f, 2f);
            if (car == null)
                return;
            Transform root = car.transform.parent;

            if (!Moving)
                localRiding = false;
            UpdateDockBlocks();
            if (SemiFunc.IsMasterClientOrSingleplayer())
                MonstersAtTheDoors(dt);
            // The camera eases after the body and at eight metres a second sits half a metre
            // behind it, which reads as sinking through the floor or floating off it. While the
            // car carries you, it follows tight.
            if (Moving && doorWait <= 0f && CameraPosition.instance != null && LocalInside())
                CameraPosition.instance.OverrideSpeed(100f, 0.1f);

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
                {
                    if (queued >= 0 && queued != atDock && SemiFunc.IsMasterClientOrSingleplayer() && TryDepart(queued))
                        queued = -1;
                    else
                    {
                        queued = -1;
                        doorsHeld = false;
                        openHold = OpenHoldTime + 1f;
                    }
                }
            }
            // Doors open by button and on arrival, and close again on their own, unless someone is
            // standing in them.
            if (openHold > 0f)
                openHold -= dt;
            if (openHold > 0f && openHold < 0.5f && DoorwayBlocked())
                openHold = 0.5f;
            float target = doorsHeld ? 0f : (openHold > 0f ? 1f : 0f);
            float open = Mathf.MoveTowards(doorOpen, target, dt * 2f);
            if (open != doorOpen)
            {
                if ((doorOpen == 0f && open > 0f) || (doorOpen == 1f && open < 1f))
                    Play(sounds?.soundActivate2, root.position);
                doorOpen = open;
                if (doorL != null) doorL.localPosition = closedL + Vector3.left * (DoorSlide * open);
                if (doorR != null) doorR.localPosition = closedR + Vector3.right * (DoorSlide * open);
                ShutBox(doorL, open == 0f);
                ShutBox(doorR, open == 0f);
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
                ShutBox(dock.LandingL, now == 0f);
                ShutBox(dock.LandingR, now == 0f);
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
            if (doorWait > 0f || doorOpen > 0f)
            {
                // Doors still closing: whoever steps in now rides too. The car never leaves
                // with a gap in them.
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
                PlayAtDocks(landThud, next, 0.4f);
                PlayAtDocks(sounds?.soundActivate3, next, 0.5f);
                SemiFunc.CameraShakeImpactDistance(next, 3f, 0.2f, 4f, 18f);
                // The whole building hears an elevator land.
                HearTheShaft();
                if (queued < 0)
                {
                    SendPursuers(next - Vector3.up * FloorOffset);
                    LandAboard(atDock);
                }
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
            if (pc == null || pc.rb == null || !Moving || doorWait > 0f)
                return;
            // Tumbling, the body on the floor is the tumble's own, and it rides the same way.
            PlayerAvatar avatar = pc.playerAvatarScript;
            if (avatar != null && avatar.isTumbling && avatar.tumble != null && avatar.tumble.physGrabObject != null && avatar.tumble.physGrabObject.rb != null)
            {
                Rigidbody body = avatar.tumble.physGrabObject.rb;
                if (!IsInside(body.position) || !Standing(body.position.y, body.velocity.y))
                    return;
                Vector3 v = body.velocity;
                v.y = CarVelocityY;
                body.velocity = v;
                return;
            }
            if (pc.rb.isKinematic || !IsInside(pc.transform.position))
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
                    ElevatorsNet.SendHatch(hatchOpen, settle: false);
                }
            }
            else if (hatchHeld)
            {
                hatchHeld = false;
                hatchWantOpen = hatchOpen > 0.45f;
                ElevatorsNet.SendHatch(hatchWantOpen ? 1f : 0f, settle: true);
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

        // Retarget a trip in flight to a dock further along. The run so far keeps its shape; only
        // the length grows, so the car cruises on instead of braking for the floor it will pass.
        private static bool Extend(int dock)
        {
            if (!Moving || dock < 0 || dock >= docks.Count)
                return false;
            float covered = Trapezoid(travelT, travelLength, out bool arrived);
            if (arrived || covered > travelLength - 6f)
                return false;
            Retarget(dock, covered);
            ElevatorsNet.SendExtend(dock);
            Plugin.LogInfo($"[Elevator] trip extended to {docks[dock].Name}");
            return true;
        }

        public static void PlayExtend(int dock)
        {
            if (!Moving || dock < 0 || dock >= docks.Count)
                return;
            Retarget(dock, Trapezoid(travelT, travelLength, out _));
        }

        // A longer run has a different profile; the clock is moved to wherever on it the car's
        // current distance falls, so the position never jumps.
        private static void Retarget(int dock, float covered)
        {
            toDock = dock;
            travelTo = docks[dock].Position + Vector3.up * FloorOffset;
            travelLength = Vector3.Distance(travelFrom, travelTo);
            float lo = 0f;
            float hi = travelLength / Mathf.Max(0.1f, Speed) + 2f * Speed / Accel + 1f;
            for (int i = 0; i < 40; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Trapezoid(mid, travelLength, out _) < covered)
                    lo = mid;
                else
                    hi = mid;
            }
            travelT = (lo + hi) * 0.5f;
        }

        // A monster that has spent a few seconds outside every room and outside the car is in
        // the void, a head that chased a rider down the shaft say. It goes back to the nearest
        // path point on the floor it is closest to.
        private static readonly Dictionary<EnemyParent, int> lostTicks = new Dictionary<EnemyParent, int>();

        private static System.Collections.IEnumerator RescueMonsters()
        {
            int rooms = LayerMask.GetMask("RoomVolume");
            while (car != null)
            {
                yield return new WaitForSeconds(2f);
                if (car == null || EnemyDirector.instance == null || !SemiFunc.IsMasterClientOrSingleplayer() || LevelGenerator.Instance == null)
                    continue;
                foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
                {
                    if (parent == null || !parent.Spawned || parent.Enemy == null)
                        continue;
                    Vector3 at = parent.Enemy.transform.position;
                    bool inRoom = Physics.CheckSphere(at + Vector3.up * 0.5f, 0.4f, rooms, QueryTriggerInteraction.Collide);
                    if (inRoom || IsInside(at) || aboard.Contains(parent))
                    {
                        lostTicks.Remove(parent);
                        continue;
                    }
                    lostTicks.TryGetValue(parent, out int ticks);
                    ticks++;
                    lostTicks[parent] = ticks;
                    if (ticks < 3)
                        continue;
                    lostTicks.Remove(parent);
                    LevelPoint? best = null;
                    float bestD = float.MaxValue;
                    foreach (LevelPoint point in LevelGenerator.Instance.LevelPathPoints)
                    {
                        if (point == null || (point.Room != null && point.Room.Truck))
                            continue;
                        float d = Vector3.Distance(point.transform.position, at) + Mathf.Abs(point.transform.position.y - at.y) * 2f;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = point;
                        }
                    }
                    if (best == null)
                        continue;
                    parent.Enemy.EnemyTeleported(best.transform.position);
                    Plugin.LogInfo($"[Elevator] {parent.enemyName} was outside the level and is back in it");
                }
            }
        }

        // The car floor joins the level's navmesh at every dock, so a monster can walk in after
        // you. Bake-only floors stand in for the car during one navmesh update and then go; a
        // carving obstacle closes each dock's patch while the car is elsewhere, so nothing paths
        // out over the open shaft.
        private static readonly List<NavMeshObstacle> dockBlocks = new List<NavMeshObstacle>();
        private static readonly List<NavMeshLink> doorLinks = new List<NavMeshLink>();
        internal static EnemyParent? LastPatrolRider;
        // Every monster the car has ever delivered somewhere this level, for the probe.
        internal static readonly HashSet<EnemyParent> Arrivals = new HashSet<EnemyParent>();
        private static readonly List<EnemyParent> aboard = new List<EnemyParent>();

        private static System.Collections.IEnumerator JoinNavMesh()
        {
            LevelGenerator lg = LevelGenerator.Instance;
            NavMeshSurface? surface = lg != null ? lg.GetComponent<NavMeshSurface>() : null;
            if (surface == null || surface.navMeshData == null)
            {
                Plugin.LogInfo("[Elevator] no navmesh surface to join, monsters stay out of the car");
                yield break;
            }
            Plugin.LogInfo($"[Elevator] navmesh surface collects {surface.collectObjects} by {surface.useGeometry}, layers {surface.layerMask.value}, agent {surface.agentTypeID}");
            // The doors stand shut while the level bakes, and a shut door is a wall to the bake:
            // every door collider goes quiet for the update or the doorways come out as gaps.
            var quiet = new List<Collider>();
            foreach ((Transform door, Collider shut) in shutBoxes)
            {
                if (door == null)
                    continue;
                foreach (Collider c in door.GetComponentsInChildren<Collider>(true))
                {
                    if (c.enabled)
                    {
                        c.enabled = false;
                        quiet.Add(c);
                    }
                }
            }
            navBaking = true;
            var stands = new List<GameObject>();
            int navOnly = LayerMask.NameToLayer("NavmeshOnly");
            foreach (Dock dock in docks)
            {
                Quaternion facing = Quaternion.Euler(0f, dock.Yaw, 0f);
                // The Museum walls its shaft mouth off from the navmesh with boxes of its own; at a
                // doorway those go, or the car floor stays an island. They touch nothing else.
                int cleared = 0;
                Vector3 mouth = dock.Position + facing * new Vector3(0f, 1f, -1.6f);
                if (navOnly >= 0)
                {
                    foreach (Collider c in Physics.OverlapBox(mouth, new Vector3(cageHalfWidth, 1.5f, 1.4f), facing, 1 << navOnly, QueryTriggerInteraction.Collide))
                    {
                        c.gameObject.SetActive(false);
                        cleared++;
                    }
                }
                foreach (NavMeshModifierVolume volume in Object.FindObjectsOfType<NavMeshModifierVolume>())
                {
                    Vector3 at = volume.transform.TransformPoint(volume.center);
                    Vector3 local = Quaternion.Inverse(facing) * (at - mouth);
                    if (Mathf.Abs(local.x) < cageHalfWidth + 1f && Mathf.Abs(local.y) < 3f && Mathf.Abs(local.z) < 2.5f)
                    {
                        volume.gameObject.SetActive(false);
                        cleared++;
                    }
                }
                Plugin.LogInfo($"[Elevator] {dock.Name} doorway: {cleared} navmesh blocker(s) cleared");
                // From a step inside the room through the doorway to the back of the car, at floor
                // height, parented under the surface so a children-only collection sees it.
                GameObject stand = GameObject.CreatePrimitive(PrimitiveType.Cube);
                stand.name = "Elevators car floor";
                stand.transform.SetParent(surface.transform, false);
                stand.transform.SetPositionAndRotation(dock.Position + facing * new Vector3(0f, -0.1f, -0.2f), facing);
                stand.transform.localScale = new Vector3(cageHalfWidth * 2f - 0.2f, 0.2f, cageHalfWidth * 2f + 1.6f);
                stand.layer = 0;
                // The surface only collects what carries a modifier.
                var modifier = stand.AddComponent<NavMeshModifier>();
                modifier.overrideArea = false;
                Renderer r = stand.GetComponent<Renderer>();
                r.enabled = surface.useGeometry == NavMeshCollectGeometry.RenderMeshes;
                r.sharedMaterial = shaftMaterial;
                stands.Add(stand);

                var block = new GameObject("Elevators dock block");
                block.transform.SetPositionAndRotation(dock.Position + facing * new Vector3(0f, 1f, 0.3f), facing);
                var obstacle = block.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.size = new Vector3(cageHalfWidth * 2f + 0.4f, 2f, cageHalfWidth * 2f + 1.2f);
                obstacle.carving = true;
                obstacle.carveOnlyStationary = false;
                obstacle.enabled = false;
                dockBlocks.Add(obstacle);
            }
            yield return null;
            AsyncOperation update = surface.UpdateNavMesh(surface.navMeshData);
            while (!update.isDone)
                yield return null;
            foreach (GameObject stand in stands)
                Object.Destroy(stand);
            navBaking = false;
            foreach (Collider c in quiet)
            {
                if (c != null)
                    c.enabled = true;
            }
            // Whatever the doors did during the bake, the shut boxes follow them again now.
            ShutBox(doorL, doorOpen == 0f);
            ShutBox(doorR, doorOpen == 0f);
            foreach (Dock dock in docks)
            {
                ShutBox(dock.LandingL, dock.LandingOpen == 0f);
                ShutBox(dock.LandingR, dock.LandingOpen == 0f);
            }
            // Whatever the mouth's geometry does to the bake, a link spans the doorway from the
            // room floor to the car floor; it only stands while both ends are on the mesh, so a
            // dock the car has left (its patch carved) has no link into the shaft.
            foreach (Dock dock in docks)
            {
                Quaternion facing = Quaternion.Euler(0f, dock.Yaw, 0f);
                var bridge = new GameObject("Elevators doorway link");
                bridge.transform.SetPositionAndRotation(dock.Position, facing);
                var link = bridge.AddComponent<NavMeshLink>();
                link.agentTypeID = surface.agentTypeID;
                link.startPoint = new Vector3(0f, 0f, -2.7f);
                link.endPoint = new Vector3(0f, 0f, -0.5f);
                link.width = Mathf.Max(0.5f, cageHalfWidth * 2f - 0.8f);
                link.bidirectional = true;
                link.autoUpdate = true;
                link.area = 0;
                doorLinks.Add(link);
            }
            bool onMesh = docks.Count > 0 && NavMesh.SamplePosition(docks[0].Position, out _, 0.6f, NavMesh.AllAreas);
            Plugin.LogInfo($"[Elevator] car floor joined the navmesh at {docks.Count} docks, first dock on mesh: {onMesh}, doorway links {doorLinks.Count}");
        }

        // Every dock's patch is closed except the one the car stands at.
        private static void UpdateDockBlocks()
        {
            for (int i = 0; i < dockBlocks.Count && i < docks.Count; i++)
            {
                bool open = i == atDock && !Moving;
                if (dockBlocks[i] != null && dockBlocks[i].enabled == open)
                    dockBlocks[i].enabled = !open;
            }
        }

        // Monsters in the car when it leaves ride with it: they are set down at the landing when
        // the doors open again. Their own pathing can't follow the floor, so they are moved.
        private static void NoteAboard()
        {
            aboard.Clear();
            if (EnemyDirector.instance == null || !SemiFunc.IsMasterClientOrSingleplayer())
                return;
            foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null)
                    continue;
                // The agent and the body it drags can stand apart; either one in the car counts.
                bool inside = IsInside(parent.Enemy.transform.position)
                    || (parent.Enemy.HasRigidbody && parent.Enemy.Rigidbody != null && IsInside(parent.Enemy.Rigidbody.transform.position));
                if (inside)
                    aboard.Add(parent);
            }
            if (aboard.Count > 0)
                Plugin.LogInfo($"[Elevator] {aboard.Count} monster(s) aboard");
        }

        private static void LandAboard(int dock)
        {
            if (aboard.Count == 0 || dock < 0 || dock >= docks.Count)
                return;
            foreach (EnemyParent parent in aboard)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null)
                    continue;
                parent.Enemy.EnemyTeleported(docks[dock].Landing);
                Arrivals.Add(parent);
                Plugin.LogInfo($"[Elevator] {parent.enemyName} stepped out with the riders at {docks[dock].Name}");
            }
            aboard.Clear();
        }

        // The shaft carries the sound to every dock: anything near one comes to look, and the
        // Huntsman, who hunts by ear, hears it from anywhere on his floor.
        private static void HearTheShaft()
        {
            if (EnemyDirector.instance == null || !SemiFunc.IsMasterClientOrSingleplayer())
                return;
            // Each monster is sent to the dock on its own floor only: a point on another floor has
            // no path, and the game's own investigate code falls over on an empty one.
            foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null || !parent.Enemy.HasStateInvestigate)
                    continue;
                bool hunter = parent.Enemy.GetComponentInChildren<EnemyHunter>(true) != null;
                int dock = NearestDock(parent.Enemy.transform.position, hunter ? 200f : 20f);
                if (dock >= 0)
                    parent.Enemy.StateInvestigate.Set(docks[dock].Landing, false);
            }
        }

        // Anyone, or anything, standing in the door line, inside or just outside the car.
        // A monster at the shut doors of the docked car presses the call button like anyone
        // would, so a car with its doors closed is no safe room. One press, then a breath.
        private static float doorsLook;
        private static float monsterPressed = -99f;
        private static readonly Dictionary<EnemyParent, float> monsterPresses = new Dictionary<EnemyParent, float>();

        private static void MonstersAtTheDoors(float dt)
        {
            doorsLook -= dt;
            if (doorsLook > 0f || Moving || atDock < 0 || doorOpen > 0f || openHold > 0f || landWait > 0f || Time.time - monsterPressed < 5f || EnemyDirector.instance == null)
                return;
            doorsLook = 0.5f;
            Vector3 landing = docks[atDock].Landing;
            foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
            {
                if (parent == null || !parent.Spawned || parent.Enemy == null)
                    continue;
                Vector3 at = parent.Enemy.transform.position;
                if (Mathf.Abs(at.y - landing.y) > 3f || Vector3.Distance(at, landing) > 3.5f)
                    continue;
                // One that loiters by the doors is not a doorman: half a minute between its presses.
                if (monsterPresses.TryGetValue(parent, out float last) && Time.time - last < 30f)
                    continue;
                monsterPresses[parent] = Time.time;
                monsterPressed = Time.time;
                foreach (LiftButton b in Object.FindObjectsOfType<LiftButton>())
                {
                    if (b.Kind == ButtonKind.Call && b.Dock == atDock && b.Attached)
                    {
                        Press(b);
                        break;
                    }
                }
                ElevatorsNet.SendDoors(OpenHoldTime);
                Plugin.LogInfo($"[Elevator] {parent.enemyName} opened the doors at {docks[atDock].Name}");
                return;
            }
        }

        private static bool DoorwayBlocked()
        {
            if (car == null)
                return false;
            foreach (PlayerAvatar player in SemiFunc.PlayerGetAll())
            {
                if (player != null && !player.isDisabled && InDoorway(player.transform.position))
                    return true;
            }
            if (EnemyDirector.instance != null)
            {
                foreach (EnemyParent parent in EnemyDirector.instance.enemiesSpawned)
                {
                    if (parent != null && parent.Spawned && parent.Enemy != null && InDoorway(parent.Enemy.transform.position))
                        return true;
                }
            }
            return false;
        }

        private static bool InDoorway(Vector3 pos)
        {
            Vector3 local = car!.transform.parent.InverseTransformPoint(pos);
            return Mathf.Abs(local.x) < cageHalfWidth && local.z > -2.6f && local.z < -0.6f && local.y > -FloorOffset - 0.6f && local.y < 2.5f;
        }

        // The next stop up or down. A shaft room's sub floor is a side stop: down from its own
        // floor reaches it, up from it goes home, and it is skipped on the way past, so up from the
        // ground is the upper floor and not the pit beneath it.
        private static int Neighbour(bool above)
        {
            if (car == null)
                return -1;
            return NextStop(car.transform.parent.position.y - FloorOffset, atDock >= 0 && IsSub(atDock), above);
        }

        private static int NeighbourOf(int dock, bool above)
        {
            if (dock < 0 || dock >= docks.Count)
                return -1;
            return NextStop(docks[dock].Position.y, IsSub(dock), above);
        }

        private static bool IsSub(int dock) => dock >= 0 && dock < docks.Count && docks[dock].Name.EndsWith(" sub");

        private static int NextStop(float y, bool fromSub, bool above)
        {
            int best = -1;
            float bestGap = float.MaxValue;
            for (int i = 0; i < docks.Count; i++)
            {
                if (IsSub(i) && (above || fromSub))
                    continue;
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
                ButtonKind.Open => "open the doors",
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

        internal static Vector3 LandingOf(string name)
        {
            Dock? d = docks.Find(x => x.Name == name);
            return d != null ? d.Landing : Vector3.zero;
        }

        // The host's word on the doors, on every client.
        public static void HoldDoors(float seconds) => openHold = seconds;

        // A client catching up: the car stands where the host says, no ride.
        public static void SnapTo(int dock)
        {
            if (car == null || Moving || dock < 0 || dock >= docks.Count || dock == atDock)
                return;
            Transform root = car.transform.parent;
            root.position = docks[dock].Position + Vector3.up * FloorOffset;
            if (body != null) body.position = root.position;
            atDock = dock;
            lastCarPos = root.position;
            Plugin.LogInfo($"[Elevator] car placed at {docks[dock].Name} to match the host");
        }

        internal static float DoorOpenAmount => doorOpen;
        internal static string DoorState() => $"open={doorOpen:0.00} held={doorsHeld} hold={openHold:0.0} land={landWait:0.0} at={atDock} to={toDock}";

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
