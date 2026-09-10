using System.Collections.Generic;
using UnityEngine;
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
        private const string ButtonName = "Museum Elevator Button";
        private const float FloorOffset = 2.15f;
        private const float DoorSlide = 1.1f;
        private const float ApproachRange = 3.2f;
        private const float Speed = 9f;
        private const float DoorTime = 0.5f;

        internal enum ButtonKind
        {
            Call,
            Up,
            Down
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
        private static GameObject? buttonAsset;
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
            buttonAsset ??= FindAsset(ButtonName);
            if (carriageAsset == null)
            {
                Plugin.LogAlways("[Elevator] the Museum elevator car is not in this build, no elevator this level");
                return;
            }
            sounds = Object.FindObjectOfType<ExtractionPoint>();
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
                if (child.name.StartsWith("Elevator Hatch") || child.name.StartsWith("Invisible Wall"))
                    child.gameObject.SetActive(false);
                else if (child.name.StartsWith("Audio Loop") && hum == null)
                    hum = child.GetComponent<AudioSource>();
                else if (child.name.StartsWith("Audio Loop"))
                    child.gameObject.SetActive(false);
            }
            if (hum != null)
            {
                hum.playOnAwake = false;
                hum.loop = true;
                hum.Stop();
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

            // Two buttons on the wall by the doors, the same mesh the Museum uses by its shaft.
            AddButton(body.transform, new Vector3(1.05f, -0.6f, -0.85f), Quaternion.Euler(0f, -90f, 0f), ButtonKind.Up, -1);
            AddButton(body.transform, new Vector3(1.05f, -0.95f, -0.85f), Quaternion.Euler(0f, -90f, 0f), ButtonKind.Down, -1);

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
                foreach (Transform child in shaft)
                {
                    if (child.name.StartsWith("Ceiling") || child.name.StartsWith("Floor") || child.name.StartsWith("Hurt Collider"))
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
            AddButton(post.transform, new Vector3(0f, 0.5f, -0.6f), Quaternion.identity, ButtonKind.Call, index);
        }

        private static void AddButton(Transform parent, Vector3 localPos, Quaternion localRot, ButtonKind kind, int dock)
        {
            GameObject button = buttonAsset != null ? Object.Instantiate(buttonAsset, parent) : GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = "Upstairs button " + kind;
            StripScripts(button);
            button.transform.localPosition = localPos;
            button.transform.localRotation = localRot;
            if (buttonAsset == null)
                button.transform.localScale = Vector3.one * 0.15f;
            var box = button.GetComponent<BoxCollider>() ?? button.AddComponent<BoxCollider>();
            if (buttonAsset != null)
                box.size = Vector3.Max(box.size, Vector3.one * 0.25f);
            var lift = button.AddComponent<LiftButton>();
            lift.Kind = kind;
            lift.Dock = dock;
        }

        // A dark box around the path between the lowest and highest dock, faces turned inward so
        // the ride is a shaft and not a look at the void between floors.
        private static void BuildShaft()
        {
            float low = float.MaxValue, high = float.MinValue;
            Vector3 centre = Vector3.zero;
            foreach (Dock d in docks)
            {
                low = Mathf.Min(low, d.Position.y);
                high = Mathf.Max(high, d.Position.y);
                centre += d.Position;
            }
            centre /= docks.Count;
            float height = high - low + 5f;
            var shaft = new GameObject("Upstairs shaft");
            shaft.transform.position = new Vector3(centre.x, (low + high) * 0.5f + FloorOffset, centre.z);
            Material? mat = car != null && car.GetComponent<Renderer>() != null ? car.GetComponent<Renderer>().sharedMaterial : null;
            for (int i = 0; i < 4; i++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.Destroy(wall.GetComponent<Collider>());
                wall.transform.SetParent(shaft.transform, false);
                Quaternion turn = Quaternion.Euler(0f, 90f * i, 0f);
                wall.transform.localPosition = turn * new Vector3(0f, 0f, 3f);
                wall.transform.localRotation = turn * Quaternion.Euler(0f, 180f, 0f);
                wall.transform.localScale = new Vector3(6f, height, 1f);
                Renderer r = wall.GetComponent<Renderer>();
                if (mat != null)
                    r.sharedMaterial = mat;
                else
                    r.material.color = new Color(0.05f, 0.05f, 0.06f);
            }
        }

        private static void StripScripts(GameObject root)
        {
            foreach (MonoBehaviour script in root.GetComponentsInChildren<MonoBehaviour>(true))
                Object.Destroy(script);
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
            float duration = Mathf.Max(1.5f, travelLength / Speed);
            travelT = Mathf.Min(1f, travelT + dt / duration);
            float eased = travelT * travelT * (3f - 2f * travelT);
            Vector3 next = Vector3.Lerp(travelFrom, travelTo, eased);
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
            if (travelT >= 1f)
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
            return Mathf.Abs(local.x) < 1.3f && Mathf.Abs(local.z) < 1.3f && local.y > -FloorOffset - 0.6f && local.y < 2.5f;
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
            bool jumping = vel.y > carVelY + 0.6f;
            if (!jumping && pos.y - floorY < 0.45f)
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
