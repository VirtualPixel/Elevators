using System;
using UnityEngine;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // The game's own level growth, so the ground floor can be held to a size and the rest of
    // the building goes up instead of out. Late levels sprawl a long way from the truck; with
    // floors, that distance turns into height.
    internal static class LevelSize
    {
        // Rooms the game would place on a level this deep into the run, before any cap.
        public static int Vanilla()
        {
            RunManager rm = RunManager.instance;
            int done = rm != null ? rm.levelsCompleted : 0;
            int amount = Mathf.Min(5 + done, 10);
            if (done >= 10)
                amount += Mathf.Min(done - 9, 5);
            return amount;
        }

        // Extraction rooms for a level of that many rooms, the game's own steps.
        public static int Extractions(int rooms)
        {
            if (rooms >= 15) return 4;
            if (rooms >= 10) return 3;
            if (rooms >= 8) return 2;
            if (rooms >= 6) return 1;
            return 0;
        }

        // The factor the generator multiplies its room count by, so the ground never grows past
        // GroundRooms. 1 leaves the level alone.
        public static float GroundFactor()
        {
            int cap = PluginConfig.GroundRooms.Value;
            if (cap <= 0 || !PluginConfig.Enabled.Value || (!PluginConfig.UpperFloor.Value && !PluginConfig.Basement.Value))
                return 1f;
            int vanilla = Vanilla();
            if (vanilla <= cap)
                return 1f;
            return cap / (float)vanilla;
        }
    }
}
