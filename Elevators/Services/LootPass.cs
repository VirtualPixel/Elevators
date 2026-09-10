using System.Collections.Generic;
using UnityEngine;
using Elevators.Configuration;

namespace Elevators.Services
{
    // Valuables for the new floors. The game's own pass only sees the ground floor (the new
    // floors' spots are hidden from it), so the ground keeps its normal spread. Then, right
    // before the game settles its prop switches, the new floors get their own share placed
    // the same way: same presets, same size weighting.
    internal static class LootPass
    {
        private static readonly List<ValuableVolume> hidden = new List<ValuableVolume>();

        public static void Reset()
        {
            hidden.Clear();
        }

        public static void HideNewFloors()
        {
            hidden.Clear();
            foreach (ValuableVolume volume in Object.FindObjectsOfType<ValuableVolume>())
            {
                if (Session.IsOnNewFloor(volume.transform.position.y))
                {
                    volume.gameObject.SetActive(false);
                    hidden.Add(volume);
                }
            }
            Plugin.LogInfo($"[Loot] {hidden.Count} spots on the new floors held back from the game's pass");
        }

        public static void FillNewFloors(ValuableDirector vd)
        {
            if (hidden.Count == 0)
                return;
            var volumes = new List<ValuableVolume>[7];
            for (int t = 0; t < 7; t++)
                volumes[t] = new List<ValuableVolume>();
            foreach (ValuableVolume volume in hidden)
            {
                if (!volume)
                    continue;
                volume.gameObject.SetActive(true);
                volumes[(int)volume.VolumeType].Add(volume);
            }
            hidden.Clear();
            for (int t = 0; t < 7; t++)
                Shuffle(volumes[t]);

            int wanted = Mathf.RoundToInt(vd.valuableTargetAmount * PluginConfig.ExtraLoot.Value / 100f);
            List<PrefabRef>[] valuables =
            {
                vd.tinyValuables, vd.smallValuables, vd.mediumValuables, vd.bigValuables,
                vd.wideValuables, vd.tallValuables, vd.veryTallValuables
            };
            string[] paths = { vd.tinyPath, vd.smallPath, vd.mediumPath, vd.bigPath, vd.widePath, vd.tallPath, vd.veryTallPath };
            int[] chances = { vd.tinyChance, vd.smallChance, vd.mediumChance, vd.bigChance, vd.wideChance, vd.tallChance, vd.veryTallChance };
            var next = new int[7];

            int spawned = 0;
            for (int i = 0; i < wanted; i++)
            {
                float best = -1f;
                int type = -1;
                for (int t = 0; t < 7; t++)
                {
                    if (next[t] >= volumes[t].Count || valuables[t] == null || valuables[t].Count == 0)
                        continue;
                    int roll = Random.Range(0, chances[t]);
                    if (roll > best)
                    {
                        best = roll;
                        type = t;
                    }
                }
                if (type == -1)
                    break;
                ValuableVolume volume = volumes[type][next[type]];
                PrefabRef valuable = valuables[type][Random.Range(0, valuables[type].Count)];
                Plugin.LogVerbose($"[Loot] {paths[type]} {valuable.PrefabName} at {volume.transform.position}");
                vd.SpawnValuable(valuable, volume, paths[type]);
                next[type]++;
                spawned++;
            }
            Plugin.LogAlways($"[Loot] {spawned} valuables on the new floors (wanted {wanted}), ground floor kept its {vd.valuableTargetAmount - spawned}");
        }

        private static void Shuffle(List<ValuableVolume> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
