using System.Collections.Generic;

namespace Upstairs.Services
{
    // One built floor: its plan, the height it sits at, and the rooms that got spawned on it.
    internal sealed class Floor
    {
        public FloorPlan Plan { get; }
        public bool Above { get; }
        public float Y;
        public float PickBottom;
        public float PickTop;
        public List<Module> Modules { get; } = new List<Module>();

        public Floor(FloorPlan plan, bool above)
        {
            Plan = plan;
            Above = above;
        }

        public string Name => Above ? "upstairs" : "basement";

        public Module? LiftModule()
        {
            foreach (Module module in Modules)
            {
                if (module && module.GridX == Plan.LiftX && module.GridY == Plan.LiftY)
                    return module;
            }
            return null;
        }
    }
}
