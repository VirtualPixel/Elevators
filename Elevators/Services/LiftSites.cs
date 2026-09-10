using System;

namespace Elevators.Services
{
    // Rooms with a real elevator shaft. Every other level gets the elevator in an opened wall of a
    // plain room; these get the car riding the room's own shaft.
    internal sealed class LiftSite
    {
        public string LevelKey { get; }
        public string ModuleName { get; }
        public FloorPlan.Kind Kind { get; }
        public bool PadAtHighestPoint { get; }
        public float PadX { get; }
        public float PadY { get; }
        public float PadZ { get; }
        public string Hint { get; }
        // The room has a real elevator shaft: the car docks inside it and the same room stands on every floor.
        public bool Shaft { get; }

        public LiftSite(string levelKey, string moduleName, FloorPlan.Kind kind, string hint, bool highest, float padX = 0f, float padY = 0f, float padZ = 0f, bool shaft = false)
        {
            Shaft = shaft;
            LevelKey = levelKey;
            ModuleName = moduleName;
            Kind = kind;
            Hint = hint;
            PadAtHighestPoint = highest;
            PadX = padX;
            PadY = padY;
            PadZ = padZ;
        }
    }

    internal static class LiftSites
    {
        public static readonly LiftSite[] All =
        {
            new LiftSite("Museum", "Module - Museum - P - 3 - Elevator To Sub Floor", FloorPlan.Kind.Passage, "in the elevator", highest: false, 1.8f, 0f, 0f, shaft: true),
        };

        public static LiftSite? For(string? levelName)
        {
            if (string.IsNullOrEmpty(levelName))
                return null;
            foreach (LiftSite site in All)
            {
                if (levelName!.IndexOf(site.LevelKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    return site;
            }
            return null;
        }
    }
}
