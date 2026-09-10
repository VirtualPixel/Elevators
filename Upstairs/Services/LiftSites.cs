using System;

namespace Upstairs.Services
{
    // Rooms that already look like they go somewhere vertical. The lift lands in one of these
    // when the level has one, so the ride starts where a player would expect it to.
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
        // The Museum elevator is an open shaft you drop down to reach its sub floor, so the pad sits in
        // the corridor right at the shaft mouth.
        // The stair rooms use their highest path point, which is the top landing.
        public static readonly LiftSite[] All =
        {
            new LiftSite("Museum", "Module - Museum - P - 3 - Elevator To Sub Floor", FloorPlan.Kind.Passage, "in the elevator", highest: false, 1.8f, 0f, 0f, shaft: true),
            new LiftSite("Manor", "Module - Manor - N - 3 - Long Stairs", FloorPlan.Kind.Normal, "at the top of the long stairs", highest: true),
            new LiftSite("Arctic", "Module - Arctic - P - 2 - Stair Corridor", FloorPlan.Kind.Passage, "at the top of the stair corridor", highest: true),
            new LiftSite("Wizard", "Module - Wizard - N - 2 - Upstairs Classroom", FloorPlan.Kind.Normal, "up in the classroom", highest: true),
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
