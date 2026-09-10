using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // The lift. A pad on each floor; stand on one and the host runs the same tumble and
    // teleport the game uses to pull a player out of the void, aimed at the pad on the
    // other floor. Crouch on the ground pad to go down when there is a basement.
    internal sealed class LiftService : MonoBehaviour
    {
        internal static LiftService? Instance { get; private set; }

        private const float PadRadius = 1.6f;
        private const float PadHeight = 1.6f;
        private const float LeaveMargin = 0.7f;
        private const float VoidPatience = 3f;

        private sealed class Pad
        {
            public Vector3 Position;
            // Somewhere to put a player near the pad that is definitely floor: the tumble and the
            // rescue land here, since a shaft dock is an open hole once the car has left it.
            public Vector3 Landing;
            public float Yaw;
            public string Name = "";
            public Pad? Up;
            public Pad? Down;
            // An extra elevator stop with no floor of its own; the tumble fallback ignores it.
            public bool Stop;
        }

        private sealed class Rider
        {
            public float CooldownUntil;
            public Pad? MustLeave;
            public float VoidTime;
        }

        private readonly List<Pad> pads = new List<Pad>();
        private readonly Dictionary<PlayerAvatar, Rider> riders = new Dictionary<PlayerAvatar, Rider>();
        private Pad? groundPad;
        private int roomMask;
        private bool active;
        private float tick;

        // FreeFly (a sibling mod) keeps a flying host outside every room on purpose; ask it before rescuing anyone.
        private static MethodInfo? flying;
        private static bool flyingResolved;

        private void Awake()
        {
            Instance = this;
            roomMask = LayerMask.GetMask("RoomVolume");
        }

        private static bool IsFlying(PlayerAvatar avatar)
        {
            if (!flyingResolved)
            {
                flyingResolved = true;
                var type = AccessTools.TypeByName("FreeFly.Services.FlyNet");
                MethodInfo? method = type == null ? null : AccessTools.Method(type, "IsFlying", new[] { typeof(PlayerAvatar) });
                flying = method != null && method.IsStatic && method.ReturnType == typeof(bool) ? method : null;
                Plugin.LogInfo(flying != null ? "[Lift] FreeFly found, flyers are never rescued" : "[Lift] FreeFly not present");
            }
            return flying != null && (bool)flying.Invoke(null, new object[] { avatar });
        }

        // What the HUD says when the local player is standing at a pad.
        internal string LocalPadStatus()
        {
            PlayerAvatar me = PlayerAvatar.instance;
            if (me == null || PadUnder(me.transform.position) == null)
                return "";
            if (!riders.TryGetValue(me, out Rider rider))
                return "ride";
            if (rider.MustLeave != null)
                return "latched";
            return Time.time < rider.CooldownUntil ? "cooldown" : "ride";
        }

        public void Clear()
        {
            active = false;
            pads.Clear();
            riders.Clear();
            groundPad = null;
            Elevator.Clear();
        }

        public void Setup()
        {
            Clear();
            Pad? ground = GroundPad();
            if (ground == null)
            {
                Plugin.LogAlways("[Lift] could not place the ground pad, no lift this level");
                return;
            }
            pads.Add(ground);
            groundPad = ground;
            AddSubFloor(Session.GroundLiftModule(), ground);

            if (Session.Up != null)
            {
                Pad? up = FloorPad(Session.Up, ground.Position);
                if (up != null)
                {
                    ground.Up = up;
                    up.Down = ground;
                    pads.Add(up);
                    AddSubFloor(Session.Up.LiftModule(), up);
                }
            }
            if (Session.Down != null)
            {
                Pad? down = FloorPad(Session.Down, ground.Position);
                if (down != null)
                {
                    ground.Down = down;
                    down.Up = ground;
                    pads.Add(down);
                    AddSubFloor(Session.Down.LiftModule(), down);
                }
            }
            active = pads.Count > 1;
            foreach (Pad pad in pads)
                Plugin.LogInfo($"[Lift] pad {pad.Name} at {pad.Position}");
            if (!active)
                return;
            var payload = new LiftPayload
            {
                Nonce = Random.Range(1, int.MaxValue),
                Level = LevelGenerator.Instance != null && LevelGenerator.Instance.Level != null ? LevelGenerator.Instance.Level.name : "",
                GroundTop = Session.GroundTop,
                GroundBottom = Session.GroundBottom,
                HasUp = Session.Up != null,
                HasDown = Session.Down != null
            };
            foreach (Pad pad in pads)
                payload.Pads.Add(new LiftPayload.Pad { Name = pad.Name, X = pad.Position.x, Y = pad.Position.y, Z = pad.Position.z, Yaw = pad.Yaw });
            UpstairsNet.Publish(payload);
            BuildCars();
        }

        // A client taking the host's pads: cars and the HUD line, no ride logic.
        public void ApplyRemote(LiftPayload payload)
        {
            Clear();
            foreach (LiftPayload.Pad p in payload.Pads)
                pads.Add(new Pad { Name = p.Name, Position = new Vector3(p.X, p.Y, p.Z), Landing = new Vector3(p.X, p.Y, p.Z), Yaw = p.Yaw });
            Pad? ground = pads.Find(p => p.Name == "ground");
            Pad? up = pads.Find(p => p.Name == "upstairs");
            Pad? down = pads.Find(p => p.Name == "basement");
            if (ground != null)
            {
                ground.Up = up;
                ground.Down = down;
                if (up != null) up.Down = ground;
                if (down != null) down.Up = ground;
            }
            BuildCars();
        }

        private void BuildCars()
        {
            var spots = new List<(Vector3, float, string)>();
            foreach (Pad pad in pads)
                spots.Add((pad.Position, pad.Yaw, pad.Name));
            Elevator.Build(spots);
        }

        private void Update()
        {
            if (SemiFunc.RunIsLevel() && Elevator.Built)
            {
                Elevator.Tick(Time.deltaTime);
                if (GameDirector.instance != null && GameDirector.instance.currentState == GameDirector.gameState.Main)
                    Elevator.CheckButtons();
            }
            if (!active || !SemiFunc.IsMasterClientOrSingleplayer() || !SemiFunc.RunIsLevel())
                return;
            if (GameDirector.instance == null || GameDirector.instance.currentState != GameDirector.gameState.Main)
                return;
            tick += Time.deltaTime;
            if (tick < 0.1f)
                return;
            float dt = tick;
            tick = 0f;

            float cooldown = PluginConfig.LiftCooldown.Value;
            bool rescue = PluginConfig.VoidRescue.Value && groundPad != null;
            foreach (PlayerAvatar avatar in SemiFunc.PlayerGetAll())
            {
                if (!avatar || avatar.isDisabled || avatar.deadSet || !avatar.tumble)
                    continue;
                if (!riders.TryGetValue(avatar, out Rider rider))
                {
                    rider = new Rider();
                    riders[avatar] = rider;
                }
                if (Time.time < rider.CooldownUntil)
                    continue;

                Vector3 pos = avatar.transform.position;
                if (rider.MustLeave != null)
                {
                    if (Flat(pos, rider.MustLeave.Position) > PadRadius + LeaveMargin || Mathf.Abs(pos.y - rider.MustLeave.Position.y) > PadHeight)
                        rider.MustLeave = null;
                    else
                        continue;
                }

                Pad? here = PadUnder(pos);
                if (here != null)
                {
                    if (here.Stop || (Elevator.Built && UpstairsNet.IsModded(avatar)))
                        continue;
                    Pad? dest = avatar.isCrouching ? (here.Down ?? here.Up) : (here.Up ?? here.Down);
                    if (dest != null)
                        Ride(avatar, rider, dest, cooldown);
                    continue;
                }

                if (rescue && !InAnyRoom(pos) && !OnNavMesh(pos) && !Elevator.IsInside(pos) && !IsFlying(avatar))
                {
                    rider.VoidTime += dt;
                    if (rider.VoidTime >= VoidPatience)
                    {
                        Plugin.LogInfo($"[Lift] {avatar.playerName} was outside every room at {pos}, sent back to the lift");
                        Ride(avatar, rider, groundPad!, cooldown);
                    }
                }
                else
                {
                    rider.VoidTime = 0f;
                }
            }
        }

        // The game's void-rescue tumble. Players with the mod ride the car instead; this is for
        // anyone without it, and for whoever ends up outside every room.
        private void Ride(PlayerAvatar avatar, Rider rider, Pad dest, float cooldown)
        {
            PlayerTumble tumble = avatar.tumble;
            tumble.TumbleSet(_isTumbling: true, _playerInput: false);
            tumble.physGrabObject.Teleport(dest.Landing + Vector3.up * 0.4f, avatar.transform.rotation);
            tumble.physGrabObject.rb.velocity = Vector3.zero;
            tumble.physGrabObject.rb.angularVelocity = Vector3.zero;
            avatar.FallDamageResetSet(2f);
            rider.CooldownUntil = Time.time + cooldown;
            rider.MustLeave = dest;
            rider.VoidTime = 0f;
            Plugin.LogInfo($"[Lift] {avatar.playerName} -> {dest.Name}");
        }

        // Hooks for the probe build that drives a headless playthrough; nothing in the game calls these.
        internal string Describe()
        {
            var parts = new List<string>();
            foreach (Pad pad in pads)
                parts.Add($"{pad.Name}@{pad.Position}");
            return parts.Count == 0 ? "no pads" : string.Join(" | ", parts);
        }

        // Nearest pad on the caller's own floor, else the nearest one anywhere.
        internal bool NearestPad(Vector3 from, float sameFloor, out Vector3 position, out string name)
        {
            position = Vector3.zero;
            name = "";
            float best = float.MaxValue;
            bool sameFloorFound = false;
            foreach (Pad pad in pads)
            {
                bool onFloor = Mathf.Abs(pad.Position.y - from.y) <= sameFloor;
                if (sameFloorFound && !onFloor)
                    continue;
                float d = Vector3.Distance(from, pad.Position);
                if ((onFloor && !sameFloorFound) || d < best)
                {
                    best = d;
                    position = pad.Position;
                    name = pad.Name;
                    sameFloorFound = sameFloorFound || onFloor;
                }
            }
            return best < float.MaxValue;
        }

        internal bool TryGetPad(string name, out Vector3 position)
        {
            foreach (Pad pad in pads)
            {
                if (pad.Name == name)
                {
                    position = pad.Position;
                    return true;
                }
            }
            position = Vector3.zero;
            return false;
        }

        internal void ProbeRide(PlayerAvatar avatar, string padName)
        {
            foreach (Pad pad in pads)
            {
                if (pad.Name != padName)
                    continue;
                if (!riders.TryGetValue(avatar, out Rider rider))
                {
                    rider = new Rider();
                    riders[avatar] = rider;
                }
                Ride(avatar, rider, pad, PluginConfig.LiftCooldown.Value);
            }
        }

        private Pad? PadUnder(Vector3 pos)
        {
            foreach (Pad pad in pads)
            {
                if (Flat(pos, pad.Position) <= PadRadius && Mathf.Abs(pos.y - pad.Position.y) <= PadHeight)
                    return pad;
            }
            return null;
        }

        private bool InAnyRoom(Vector3 pos)
        {
            return Physics.CheckSphere(pos + Vector3.up * 0.6f, 0.25f, roomMask, QueryTriggerInteraction.Collide);
        }

        // Standing on the navmesh means standing somewhere the level meant you to be, room volume or not.
        private static bool OnNavMesh(Vector3 pos)
        {
            return NavMesh.SamplePosition(pos, out NavMeshHit hit, 1.2f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - pos.y) < 1.2f;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static Pad? GroundPad()
        {
            Module? module = Session.GroundLiftModule();
            if (module == null)
                return null;
            LiftSite? site = Session.Site;
            Vector3 spot;
            if (site != null && site.Shaft && ShaftDock(module, out Vector3 dock, out float shaftYaw))
            {
                NearestPoint(module, dock, out Vector3 landing);
                return new Pad { Position = dock, Landing = OnNavMesh(landing, 2f), Name = "ground", Yaw = shaftYaw };
            }
            if (Session.LiftSide >= 0)
                return Doorway(module, "ground");
            if (site != null && !site.PadAtHighestPoint)
                spot = module.transform.TransformPoint(new Vector3(site.PadX, site.PadY, site.PadZ));
            else if (site != null && site.PadAtHighestPoint && HighestPoint(module, out Vector3 top))
                spot = top;
            else if (!NearestPoint(module, module.transform.position, out spot))
                return null;
            Vector3 at = OnNavMesh(spot, 2f);
            float yaw = FacingAway(module.transform.position, at);
            AgainstWall(ref at, ref yaw);
            return new Pad { Position = at, Landing = at, Name = "ground", Yaw = yaw };
        }

        private static Pad? FloorPad(Floor floor, Vector3 groundSpot)
        {
            Module? module = floor.LiftModule();
            if (module == null)
                return null;
            Vector3 center = module.transform.position;
            if (Session.LiftSide >= 0)
                return Doorway(module, floor.Name);
            if (Session.Site != null && Session.Site.Shaft && ShaftDock(module, out Vector3 dock, out float shaftYaw))
            {
                NearestPoint(module, dock, out Vector3 landing);
                return new Pad { Position = dock, Landing = OnNavMesh(landing, 2f), Name = floor.Name, Yaw = shaftYaw };
            }
            if (!NearestPoint(module, new Vector3(groundSpot.x, center.y, groundSpot.z), out Vector3 spot))
                spot = OnNavMesh(center, 6f);
            float yaw = FacingAway(center, spot);
            AgainstWall(ref spot, ref yaw);
            return new Pad { Position = spot, Landing = spot, Name = floor.Name, Yaw = yaw };
        }

        // The Museum elevator room has a sub floor 8 m down that the shaft used to drop you into. With
        // the car in the shaft that floor needs a stop of its own, or its loot is out of reach.
        private void AddSubFloor(Module? module, Pad main)
        {
            if (module == null || Session.Site == null || !Session.Site.Shaft || !module.name.StartsWith(Session.Site.ModuleName))
                return;
            float floorY = module.transform.position.y;
            float lowest = floorY;
            Vector3 landing = Vector3.zero;
            foreach (LevelPoint point in module.GetComponentsInChildren<LevelPoint>())
            {
                if (point.transform.position.y < lowest)
                {
                    lowest = point.transform.position.y;
                    landing = point.transform.position;
                }
            }
            if (floorY - lowest < 4f)
                return;
            var sub = new Pad
            {
                Position = new Vector3(main.Position.x, lowest, main.Position.z),
                Landing = landing,
                Name = main.Name + " sub",
                Yaw = main.Yaw,
                Stop = true
            };
            pads.Add(sub);
        }

        // The car stands just outside the opened wall, doors facing into the room, and the landing
        // is the floor right inside the opening.
        private static Pad Doorway(Module module, string name)
        {
            Vector3 dir = Session.SideDirection(Session.LiftSide);
            Vector3 center = module.transform.position;
            float half = LevelGenerator.ModuleWidth * LevelGenerator.TileSize * 0.5f;
            Vector3 dock = center + dir * (half + 1.55f);
            Vector3 landing = OnNavMesh(center + dir * (half - 1.2f), 2f);
            return new Pad { Position = dock, Landing = landing, Name = name, Yaw = Quaternion.LookRotation(dir).eulerAngles.y };
        }

        // Where the room's own elevator car sits, at the room's floor level, turned the way the car is.
        private static bool ShaftDock(Module module, out Vector3 dock, out float yaw)
        {
            foreach (Transform t in module.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != Elevator.CarriageName)
                    continue;
                dock = new Vector3(t.position.x, module.transform.position.y, t.position.z);
                yaw = t.eulerAngles.y;
                return true;
            }
            dock = Vector3.zero;
            yaw = 0f;
            return false;
        }

        private static bool HighestPoint(Module module, out Vector3 spot)
        {
            spot = Vector3.zero;
            bool found = false;
            foreach (LevelPoint point in module.GetComponentsInChildren<LevelPoint>())
            {
                if (!found || point.transform.position.y > spot.y)
                {
                    spot = point.transform.position;
                    found = true;
                }
            }
            return found;
        }

        private static bool NearestPoint(Module module, Vector3 near, out Vector3 spot)
        {
            spot = Vector3.zero;
            float best = float.MaxValue;
            foreach (LevelPoint point in module.GetComponentsInChildren<LevelPoint>())
            {
                float d = Flat(point.transform.position, near);
                if (d < best)
                {
                    best = d;
                    spot = point.transform.position;
                }
            }
            return best < float.MaxValue;
        }

        // A car in the middle of a room looks dropped there. Slide it back to the nearest wall within
        // reach, doors toward the room, as long as the floor there is still walkable.
        private static void AgainstWall(ref Vector3 spot, ref float yaw)
        {
            int mask = LayerMask.GetMask("Default");
            Vector3 eye = spot + Vector3.up * 1.2f;
            float best = 7f;
            Vector3 hitPoint = Vector3.zero;
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, 22.5f * i, 0f) * Vector3.forward;
                if (Physics.Raycast(eye, dir, out RaycastHit hit, best, mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.normal.y) < 0.3f)
                {
                    best = hit.distance;
                    hitPoint = hit.point;
                    normal = hit.normal;
                }
            }
            if (normal == Vector3.zero)
                return;
            Vector3 flat = new Vector3(normal.x, 0f, normal.z).normalized;
            Vector3 back = new Vector3(hitPoint.x, spot.y, hitPoint.z) + flat * 1.7f;
            if (!NavMesh.SamplePosition(back, out NavMeshHit onFloor, 1f, NavMesh.AllAreas) || Mathf.Abs(onFloor.position.y - spot.y) > 0.5f)
                return;
            spot = new Vector3(back.x, onFloor.position.y, back.z);
            yaw = Quaternion.LookRotation(-flat).eulerAngles.y;
        }

        // The car's doors are on its back, so a car turned away from the room opens toward it.
        private static float FacingAway(Vector3 center, Vector3 spot)
        {
            Vector3 d = spot - center;
            d.y = 0f;
            return d.sqrMagnitude < 0.01f ? 0f : Quaternion.LookRotation(d).eulerAngles.y;
        }

        private static Vector3 OnNavMesh(Vector3 spot, float reach)
        {
            return NavMesh.SamplePosition(spot, out NavMeshHit hit, reach, NavMesh.AllAreas) ? hit.position : spot;
        }
    }
}
