using System.Collections.Generic;
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
        internal sealed class LiftButton : MonoBehaviour
        {
            public ButtonKind Kind;
            public int Dock;
        }

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
        private static Vector3 lastCarPos;
        private static bool localRiding;
        private static AudioSource? hum;
        private static Rigidbody? body;
        private static float cageHalfWidth = 1.7f;
        private static Transform? hatch;
        private static Quaternion hatchClosed;
        private static float hatchOpen;
        private static bool hatchWantOpen;
        private static readonly List<Collider> riderColliders = new List<Collider>();

        public static bool Built => car != null;
        public static bool Moving => toDock >= 0;
        public static int CurrentDock => atDock;
        public static int DockCount => docks.Count;

        public static void Clear()
        {
            if (car != null)
                Object.Destroy(car.transform.parent != null ? car.transform.parent.gameObject : car);
            foreach (GameObject g in GameObject.FindGameObjectsWithTag("Untagged"))
            {
                if (g.name == "Upstairs dock" || g.name == "Upstairs shaft")
                    Object.Destroy(g);
            }
            RiderCollision(true);
            car = null;
            doorL = doorR = null;
            hum = null;
            hatch = null;
            hatchOpen = 0f;
            hatchWantOpen = false;
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
                if (child.name.StartsWith("Elevator Hatch"))
                {
                    // The roof hatch stays, as a small door you grab to swing open and shut.
                    hatch = child.Find("Hinge Template");
                    if (hatch != null)
                    {
                        hatchClosed = hatch.localRotation;
                        foreach (Collider c in hatch.GetComponentsInChildren<Collider>())
                        {
                            var lift = c.gameObject.AddComponent<LiftButton>();
                            lift.Kind = ButtonKind.Hatch;
                        }
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
                AudioClip clip = hum.clip;
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
                motor.volume = 0.9f;
                motor.outputAudioMixerGroup = hum.outputAudioMixerGroup != null ? hum.outputAudioMixerGroup : (AudioManager.instance != null ? AudioManager.instance.SoundMasterGroup : null);
                hum = motor;
                Plugin.LogInfo($"[Elevator] motor clip: {(clip != null ? clip.name : "none")}");
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
            Transform panel = Panel(body.transform, new Vector3(wallX - 0.02f, -0.5f, -0.6f), Quaternion.LookRotation(Vector3.left), 0.5f);
            AddButton(panel, new Vector3(0f, 0.12f, 0.035f), ButtonKind.Up, -1, "UP");
            AddButton(panel, new Vector3(0f, -0.12f, 0.035f), ButtonKind.Down, -1, "DOWN");
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
                if (dock.LandingL != null) { dock.LandingClosedL = ClosedAt(dock.LandingL, left: true) + Vector3.back * 0.18f; dock.LandingL.localPosition = dock.LandingClosedL; }
                if (dock.LandingR != null) { dock.LandingClosedR = ClosedAt(dock.LandingR, left: false) + Vector3.back * 0.18f; dock.LandingR.localPosition = dock.LandingClosedR; }
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

        private static void BuildCallButton(int index)
        {
            Dock dock = docks[index];
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Upstairs dock";
            post.transform.localScale = new Vector3(0.12f, 1.2f, 0.12f);
            Quaternion facing = Quaternion.Euler(0f, dock.Yaw, 0f);
            post.transform.SetPositionAndRotation(dock.Position + facing * new Vector3(1.9f, 0.6f, -1.6f), facing);
            Renderer carRenderer = car!.GetComponent<Renderer>();
            if (carRenderer != null)
                post.GetComponent<Renderer>().sharedMaterial = carRenderer.sharedMaterial;
            // Faces the room, which is the car's -z.
            Transform plate = Panel(post.transform, new Vector3(0f, 0.5f, -0.6f), Quaternion.Euler(0f, 180f, 0f), 0.3f);
            AddButton(plate, new Vector3(0f, 0f, 0.035f), ButtonKind.Call, index, "CALL");
            var glow = new GameObject("call light").AddComponent<Light>();
            glow.transform.SetParent(post.transform, false);
            glow.transform.localPosition = new Vector3(0f, 0.6f, -1.2f);
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.75f, 0.45f);
            glow.intensity = 0.8f;
            glow.range = 1.8f;
            glow.shadows = LightShadows.None;
        }

        private static Material? plateMaterial;
        private static Material? buttonMaterial;

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

        private static void AddButton(Transform panel, Vector3 localPos, ButtonKind kind, int dock, string label)
        {
            buttonMaterial ??= new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.82f, 0.45f) };
            var button = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            button.name = "Upstairs button " + kind;
            button.transform.SetParent(panel, false);
            button.transform.localPosition = localPos;
            button.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            button.transform.localScale = new Vector3(0.14f, 0.03f, 0.14f);
            button.GetComponent<Renderer>().sharedMaterial = buttonMaterial;
            Collider round = button.GetComponent<Collider>();
            Object.Destroy(round);
            var box = button.AddComponent<BoxCollider>();
            box.size = new Vector3(2.2f, 2.2f, 2.2f);
            var lift = button.AddComponent<LiftButton>();
            lift.Kind = kind;
            lift.Dock = dock;

            Transform text = NativeText.CreateWorld(panel, label, 0.22f);
            text.localPosition = localPos + new Vector3(0f, -0.1f, 0f);
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
                Object.Destroy(script);
            foreach (Joint joint in root.GetComponentsInChildren<Joint>(true))
                Object.Destroy(joint);
            foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true))
                Object.Destroy(rb);
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
            if (localRiding)
                RiderCollision(false);
            Play(sounds?.soundActivate1, root.position);
            if (hum != null)
                hum.Play();
            Plugin.LogInfo($"[Elevator] {docks[from].Name} -> {docks[to].Name}, {travelLength:0} m, rider={localRiding}");
        }

        public static void Tick(float dt)
        {
            if (car == null)
                return;
            Transform root = car.transform.parent;

            if (!Moving)
                localRiding = false;

            if (hatch != null)
            {
                float h = Mathf.MoveTowards(hatchOpen, hatchWantOpen ? 1f : 0f, dt * 2.5f);
                if (h != hatchOpen)
                {
                    hatchOpen = h;
                    hatch.localRotation = hatchClosed * Quaternion.Euler(-110f * h, 0f, 0f);
                }
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
                doorWait -= dt;
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
            if (localRiding)
            {
                // The game switches the avatar's collider back on every frame, so it goes off again
                // right before each physics step for as long as the ride lasts.
                RiderCollision(false);
                Carry(delta, next.y - FloorOffset, delta.y / dt);
            }
            lastCarPos = next;
            if (arrived)
            {
                atDock = toDock;
                toDock = -1;
                doorsHeld = false;
                if (hum != null)
                    hum.Stop();
                RiderCollision(true);
                Play(sounds?.soundActivate3, next);
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

        // Outside a real shaft the trip passes through whatever roof or floor sits in the way. The
        // rider's own colliders come off for the ride, since the car floor snap is what holds them.
        private static void RiderCollision(bool on)
        {
            if (on)
            {
                foreach (Collider c in riderColliders)
                    if (c) c.enabled = true;
                riderColliders.Clear();
                return;
            }
            foreach (Component? root in new Component?[] { PlayerController.instance, PlayerAvatar.instance })
            {
                if (root == null)
                    continue;
                foreach (Collider c in root.GetComponentsInChildren<Collider>())
                {
                    if (c.isTrigger)
                        continue;
                    if (!riderColliders.Contains(c))
                        riderColliders.Add(c);
                    c.enabled = false;
                }
            }
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

        private static void Carry(Vector3 delta, float floorY, float carVelY)
        {
            PlayerController pc = PlayerController.instance;
            if (pc == null || pc.rb == null)
                return;
            Vector3 pos = pc.rb.position + delta;
            Vector3 vel = pc.rb.velocity;
            // Only catch feet that are on or through the floor and coming down; a jump is left to gravity.
            bool landing = vel.y <= carVelY + 0.05f && pos.y - floorY < 0.12f;
            if (landing)
            {
                pos.y = floorY + 0.02f;
                vel.y = carVelY;
                pc.rb.velocity = vel;
            }
            pc.rb.position = pos;
            pc.transform.position = pos;
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
            if (button == null)
                return;
            if (button.Kind == ButtonKind.Hatch)
            {
                hatchWantOpen = !hatchWantOpen;
                Play(sounds?.soundActivate2, hit.point);
                return;
            }
            int target = button.Kind switch
            {
                ButtonKind.Call => button.Dock,
                ButtonKind.Up => Neighbour(above: true),
                _ => Neighbour(above: false)
            };
            if (target < 0)
                return;
            Play(sounds?.soundButton, hit.point);
            UpstairsNet.RequestRide(target);
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
                ButtonKind.Hatch => hatchWantOpen ? "close the hatch" : "open the hatch",
                _ => "call"
            };
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
