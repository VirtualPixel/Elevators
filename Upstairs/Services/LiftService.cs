using System.Collections.Generic;
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

        private const float PadRadius = 1.3f;
        private const float PadHeight = 1.6f;
        private const float LeaveMargin = 0.7f;
        private const float VoidPatience = 1.5f;

        private sealed class Pad
        {
            public Vector3 Position;
            public string Name = "";
            public Pad? Up;
            public Pad? Down;
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
        private float voidLowFrom, voidLowTo, voidHighFrom, voidHighTo;
        private bool active;
        private float tick;

        private void Awake() => Instance = this;

        public void Clear()
        {
            active = false;
            pads.Clear();
            riders.Clear();
            groundPad = null;
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

            voidLowFrom = voidLowTo = voidHighFrom = voidHighTo = 0f;
            if (Session.Up != null)
            {
                Pad? up = FloorPad(Session.Up, ground.Position);
                if (up != null)
                {
                    ground.Up = up;
                    up.Down = ground;
                    pads.Add(up);
                    voidHighFrom = Session.GroundTop + 1f;
                    voidHighTo = Session.Up.Y + Session.Up.PickBottom - 1f;
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
                    voidLowFrom = Session.Down.Y + Session.Down.PickTop + 1f;
                    voidLowTo = Session.GroundBottom - 1f;
                }
            }
            active = pads.Count > 1;
            foreach (Pad pad in pads)
                Plugin.LogInfo($"[Lift] pad {pad.Name} at {pad.Position}");
        }

        private void Update()
        {
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
                    Pad? dest = avatar.isCrouching ? (here.Down ?? here.Up) : (here.Up ?? here.Down);
                    if (dest != null)
                        Ride(avatar, rider, dest, cooldown);
                    continue;
                }

                if (groundPad != null && InVoid(pos.y))
                {
                    rider.VoidTime += dt;
                    if (rider.VoidTime >= VoidPatience)
                    {
                        Plugin.LogInfo($"[Lift] {avatar.playerName} fell between floors at y={pos.y:0.0}, sent back to the lift");
                        Ride(avatar, rider, groundPad, cooldown);
                    }
                }
                else
                {
                    rider.VoidTime = 0f;
                }
            }
        }

        private void Ride(PlayerAvatar avatar, Rider rider, Pad dest, float cooldown)
        {
            PlayerTumble tumble = avatar.tumble;
            tumble.TumbleSet(_isTumbling: true, _playerInput: false);
            tumble.physGrabObject.Teleport(dest.Position + Vector3.up * 0.4f, avatar.transform.rotation);
            tumble.physGrabObject.rb.velocity = Vector3.zero;
            tumble.physGrabObject.rb.angularVelocity = Vector3.zero;
            avatar.FallDamageResetSet(2f);
            rider.CooldownUntil = Time.time + cooldown;
            rider.MustLeave = dest;
            rider.VoidTime = 0f;
            Plugin.LogInfo($"[Lift] {avatar.playerName} -> {dest.Name}");
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

        private bool InVoid(float y)
        {
            return (voidHighTo > voidHighFrom && y > voidHighFrom && y < voidHighTo)
                || (voidLowTo > voidLowFrom && y > voidLowFrom && y < voidLowTo);
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
            if (site != null && !site.PadAtHighestPoint)
                spot = module.transform.TransformPoint(new Vector3(site.PadX, site.PadY, site.PadZ));
            else if (site != null && site.PadAtHighestPoint && HighestPoint(module, out Vector3 top))
                spot = top;
            else if (!NearestPoint(module, module.transform.position, out spot))
                return null;
            return new Pad { Position = OnNavMesh(spot, 2f), Name = "ground" };
        }

        private static Pad? FloorPad(Floor floor, Vector3 groundSpot)
        {
            Module? module = floor.LiftModule();
            if (module == null)
                return null;
            Vector3 center = module.transform.position;
            if (!NearestPoint(module, new Vector3(groundSpot.x, center.y, groundSpot.z), out Vector3 spot))
                spot = OnNavMesh(center, 6f);
            return new Pad { Position = spot, Name = floor.Name };
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

        private static Vector3 OnNavMesh(Vector3 spot, float reach)
        {
            return NavMesh.SamplePosition(spot, out NavMeshHit hit, reach, NavMesh.AllAreas) ? hit.position : spot;
        }
    }
}
