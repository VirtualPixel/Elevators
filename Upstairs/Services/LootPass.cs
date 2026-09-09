using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // Extra valuables for the new floors, placed the way the game places its own: same
    // presets, same size weighting, only into spots the game has not already filled.
    internal static class LootPass
    {
        internal static readonly HashSet<ValuableVolume> Used = new HashSet<ValuableVolume>();

        public static IEnumerator After(ValuableDirector vd, IEnumerator original)
        {
            while (original.MoveNext())
                yield return original.Current;
            IEnumerator extra = Run(vd);
            while (extra.MoveNext())
                yield return extra.Current;
        }

        private static IEnumerator Run(ValuableDirector vd)
        {
            int wanted = Mathf.RoundToInt(vd.valuableTargetAmount * PluginConfig.ExtraLoot.Value / 100f);
            if (wanted <= 0)
                yield break;

            List<ValuableVolume>[] volumes =
            {
                Open(vd.tinyVolumes), Open(vd.smallVolumes), Open(vd.mediumVolumes), Open(vd.bigVolumes),
                Open(vd.wideVolumes), Open(vd.tallVolumes), Open(vd.veryTallVolumes)
            };
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
                    if (next[t] >= volumes[t].Count || valuables[t].Count == 0)
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
                vd.SpawnValuable(valuable, volume, paths[type]);
                next[type]++;
                spawned++;
                yield return null;
            }
            Plugin.LogAlways($"[Loot] {spawned} extra valuables on the new floors (asked for {wanted}, game placed {vd.valuableTargetAmount - spawned})");
        }

        private static List<ValuableVolume> Open(List<ValuableVolume> all)
        {
            var open = new List<ValuableVolume>();
            foreach (ValuableVolume v in all)
            {
                if (v && !Used.Contains(v) && Session.IsOnNewFloor(v.transform.position.y))
                    open.Add(v);
            }
            return open;
        }
    }
}
