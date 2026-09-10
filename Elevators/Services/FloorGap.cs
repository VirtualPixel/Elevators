using System;

namespace Elevators.Services
{
    // How far apart two floors have to be so nothing in one pokes into the other. Rooms in
    // this game are tall: catwalks go 25 m up, pits go 28 m down, so the gap is measured
    // per level instead of guessed.
    internal static class FloorGap
    {
        public const float Margin = 6f;
        public const float MapLayer = 4f;
        public const float Minimum = 12f;

        // lowerTop: the highest point of the floor underneath. upperBottom: the lowest point of
        // the rooms going on top, relative to their own floor height.
        public static float Compute(float lowerTop, float upperBottom)
        {
            float needed = lowerTop - upperBottom + Margin;
            float snapped = (float)Math.Ceiling(needed / MapLayer) * MapLayer;
            return Math.Max(Minimum, snapped);
        }

        // Cells on a new floor count toward the level's module total so every client waits for them.
        public static int CellCount(int groundCells, int percent)
        {
            return Math.Max(2, (int)Math.Round(groundCells * percent / 100.0));
        }
    }
}
