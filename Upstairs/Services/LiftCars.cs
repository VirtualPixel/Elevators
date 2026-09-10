using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Upstairs.Services
{
    // The elevator you see. The Museum's own elevator car, lifted out of its module prefab and
    // stood on each lift pad, on every client. Doors slide open when someone walks up, shut for
    // the ride, and open again on the floor you arrive at. The car is the game's own asset, so it
    // looks like it belongs; the shaft is the part we skip, since the floors are 40 m apart.
    internal static class LiftCars
    {
        private const string SourceModule = "Module - Museum - P - 3 - Elevator To Sub Floor";
        private const string CarriagePath = "Elevator/Museum Elevator Carriage";
        private const float FloorOffset = 2.15f;
        private const float DoorSlide = 1.1f;
        private const float ApproachRange = 3.2f;
        private const float FadeHold = 1.2f;
        private const float MoveAt = 0.9f;

        private sealed class Car
        {
            public GameObject Root = null!;
            public Vector3 Pad;
            public Transform? DoorL;
            public Transform? DoorR;
            public Vector3 ClosedL;
            public Vector3 ClosedR;
            public float Open;
            public bool Hold;
        }

        private static readonly List<Car> cars = new List<Car>();
        private static GameObject? source;
        private static ExtractionPoint? sounds;

        public static int Count => cars.Count;

        public static void Clear()
        {
            foreach (Car car in cars)
            {
                if (car.Root)
                    Object.Destroy(car.Root);
            }
            cars.Clear();
            sounds = null;
        }

        public static void Build(IReadOnlyList<(Vector3 pad, float yaw)> pads)
        {
            Clear();
            source ??= FindSource();
            if (source == null)
            {
                Plugin.LogAlways("[Cars] the Museum elevator car is not in this build, pads stay bare");
                return;
            }
            sounds = Object.FindObjectOfType<ExtractionPoint>();
            foreach ((Vector3 pad, float yaw) in pads)
                cars.Add(Stand(pad, yaw));
            Plugin.LogInfo($"[Cars] {cars.Count} elevator cars placed");
        }

        private static Car Stand(Vector3 pad, float yaw)
        {
            var holder = new GameObject("Upstairs lift");
            holder.SetActive(false);
            GameObject body = Object.Instantiate(source!, holder.transform);
            body.name = "Museum Elevator Carriage";
            foreach (MonoBehaviour script in body.GetComponentsInChildren<MonoBehaviour>(true))
                Object.Destroy(script);
            foreach (Transform child in body.transform)
            {
                if (child.name.StartsWith("Elevator Hatch") || child.name.StartsWith("Audio Loop") || child.name.StartsWith("Invisible Wall"))
                    child.gameObject.SetActive(false);
            }
            Transform cables = body.transform.Find("Cables");
            if (cables != null)
            {
                for (int i = 0; i < cables.childCount; i++)
                    cables.GetChild(i).gameObject.SetActive(cables.GetChild(i).localPosition.y < 3f);
            }
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            holder.transform.SetPositionAndRotation(pad + Vector3.up * FloorOffset, Quaternion.Euler(0f, yaw, 0f));
            holder.SetActive(true);

            var car = new Car { Root = holder, Pad = pad };
            car.DoorL = body.transform.Find("Museum Elevator door (1)");
            car.DoorR = body.transform.Find("Museum Elevator door");
            if (car.DoorL != null) car.ClosedL = car.DoorL.localPosition;
            if (car.DoorR != null) car.ClosedR = car.DoorR.localPosition;
            return car;
        }

        private static GameObject? FindSource()
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
                        if (pr == null || pr.PrefabName != SourceModule || !pr.IsValid())
                            continue;
                        Transform? carriage = pr.Prefab != null ? pr.Prefab.transform.Find(CarriagePath) : null;
                        return carriage != null ? carriage.gameObject : null;
                    }
                }
            }
            return null;
        }

        // Doors follow whoever is near, unless a ride is holding them shut.
        public static void Tick(float dt)
        {
            if (cars.Count == 0)
                return;
            List<PlayerAvatar> players = SemiFunc.PlayerGetAll();
            foreach (Car car in cars)
            {
                bool near = false;
                if (!car.Hold)
                {
                    foreach (PlayerAvatar player in players)
                    {
                        if (!player || player.isDisabled)
                            continue;
                        Vector3 d = player.transform.position - car.Pad;
                        if (Mathf.Abs(d.y) < 3f && new Vector2(d.x, d.z).magnitude < ApproachRange)
                        {
                            near = true;
                            break;
                        }
                    }
                }
                float target = car.Hold ? 0f : (near ? 1f : 0f);
                float open = Mathf.MoveTowards(car.Open, target, dt * 2f);
                if (open == car.Open)
                    continue;
                car.Open = open;
                if (car.DoorL != null) car.DoorL.localPosition = car.ClosedL + Vector3.left * (DoorSlide * open);
                if (car.DoorR != null) car.DoorR.localPosition = car.ClosedR + Vector3.right * (DoorSlide * open);
            }
        }

        public static void PlayRide(int actor, int from, int to)
        {
            if (to < 0 || to >= cars.Count)
                return;
            Plugin.Instance.StartCoroutine(Ride(actor == UpstairsNet.LocalActor, from >= 0 && from < cars.Count ? cars[from] : null, cars[to]));
        }

        private static IEnumerator Ride(bool local, Car? from, Car to)
        {
            if (from != null)
            {
                from.Hold = true;
                Play(sounds?.soundActivate1, from.Pad);
            }
            if (local && CameraTopFade.Instance != null)
                CameraTopFade.Instance.Set(1f, FadeHold);
            to.Hold = true;
            yield return new WaitForSeconds(MoveAt);
            if (local)
                MoveLocal(to.Pad + Vector3.up * 0.05f);
            Play(sounds?.soundActivate3, to.Pad);
            yield return new WaitForSeconds(0.4f);
            to.Hold = false;
            yield return new WaitForSeconds(1f);
            if (from != null)
                from.Hold = false;
        }

        // The same moves the game's own SpawnRPC makes for the local player, minus the RPC a
        // client is not allowed to send.
        private static void MoveLocal(Vector3 pos)
        {
            PlayerController pc = PlayerController.instance;
            PlayerAvatar me = PlayerAvatar.instance;
            if (pc == null || me == null)
                return;
            pc.rb.velocity = Vector3.zero;
            pc.transform.position = pos;
            pc.rb.position = pos;
            me.transform.position = pos;
            if (me.rb) me.rb.position = pos;
            me.clientPosition = pos;
            me.clientPositionCurrent = pos;
        }

        private static void Play(Sound? sound, Vector3 at)
        {
            if (sound != null)
                sound.Play(at);
        }
    }
}
