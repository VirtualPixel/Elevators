using System;
using System.Collections.Generic;

namespace Upstairs.Services
{
    // Which ground cells get a room stacked on them, what kind of room, how it is turned,
    // and where the doors between them go. Pure grid math, no Unity.
    internal sealed class FloorPlan
    {
        public enum Kind
        {
            Normal,
            Passage,
            DeadEnd
        }

        public sealed class Cell
        {
            public int X;
            public int Y;
            public Kind Kind;
            public float RotationY;
            public bool Top;
            public bool Bottom;
            public bool Right;
            public bool Left;

            public int Connections => (Top ? 1 : 0) + (Bottom ? 1 : 0) + (Right ? 1 : 0) + (Left ? 1 : 0);
        }

        // A door sits on a cell's top edge (toward y+1) or its right edge (toward x+1).
        public readonly struct Door
        {
            public readonly int X;
            public readonly int Y;
            public readonly bool RightEdge;

            public Door(int x, int y, bool rightEdge)
            {
                X = x;
                Y = y;
                RightEdge = rightEdge;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public int LiftX { get; }
        public int LiftY { get; }
        public List<Cell> Cells { get; } = new List<Cell>();
        public List<Door> Doors { get; } = new List<Door>();

        private readonly Cell?[,] grid;

        private FloorPlan(int width, int height, int liftX, int liftY)
        {
            Width = width;
            Height = height;
            LiftX = liftX;
            LiftY = liftY;
            grid = new Cell?[width, height];
        }

        public Cell? At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return null;
            return grid[x, y];
        }

        // Grows a connected patch of cells out from the lift cell, only ever on top of active
        // ground cells, then classifies each cell the way the game's own generator would.
        // liftAxisVertical: pin the lift cell as a passage whose doors only face along that axis, so the
        // same elevator module can stand on every floor with its shafts lined up. Null leaves it free.
        public static FloorPlan Build(bool[,] ground, int liftX, int liftY, int count, int passageMax, Random rng, bool? liftAxisVertical = null)
        {
            int width = ground.GetLength(0);
            int height = ground.GetLength(1);
            var plan = new FloorPlan(width, height, liftX, liftY);
            if (!ground[liftX, liftY])
                return plan;

            var taken = new bool[width, height];
            var picked = new List<(int x, int y)> { (liftX, liftY) };
            taken[liftX, liftY] = true;
            int target = Math.Min(count, Reachable(ground, liftX, liftY));

            int stalls = 0;
            while (picked.Count < target && stalls < 500)
            {
                var (cx, cy) = picked[rng.Next(picked.Count)];
                int dir = rng.Next(4);
                int tx = cx + (dir == 0 ? -1 : dir == 1 ? 1 : 0);
                int ty = cy + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                if (tx < 0 || ty < 0 || tx >= width || ty >= height || !ground[tx, ty] || taken[tx, ty])
                {
                    stalls++;
                    continue;
                }
                taken[tx, ty] = true;
                picked.Add((tx, ty));
                stalls = 0;
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (!taken[x, y])
                        continue;
                    plan.grid[x, y] = new Cell
                    {
                        X = x,
                        Y = y,
                        Top = y + 1 < height && taken[x, y + 1],
                        Bottom = y - 1 >= 0 && taken[x, y - 1],
                        Right = x + 1 < width && taken[x + 1, y],
                        Left = x - 1 >= 0 && taken[x - 1, y]
                    };
                }
            }

            Cell lift = plan.grid[liftX, liftY]!;
            if (liftAxisVertical == true)
            {
                Unlink(plan, lift, left: true);
                Unlink(plan, lift, right: true);
            }
            else if (liftAxisVertical == false)
            {
                Unlink(plan, lift, top: true);
                UnlinkBottom(plan, lift);
            }

            int passages = 0;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    Cell? cell = plan.grid[x, y];
                    if (cell == null)
                        continue;
                    if (cell == lift && liftAxisVertical != null)
                    {
                        cell.Kind = Kind.Passage;
                        cell.RotationY = liftAxisVertical == true ? 0f : 90f;
                        passages++;
                    }
                    else
                    {
                        Classify(cell, ref passages, passageMax, rng);
                    }
                    plan.Cells.Add(cell);
                    if (cell.Top)
                        plan.Doors.Add(new Door(x, y, rightEdge: false));
                    if (cell.Right)
                        plan.Doors.Add(new Door(x, y, rightEdge: true));
                }
            }
            return plan;
        }

        private static void Unlink(FloorPlan plan, Cell lift, bool left = false, bool top = false, bool right = false)
        {
            if (left || right)
            {
                Cell? side = plan.At(lift.X + (left ? -1 : 1), lift.Y);
                if (left) lift.Left = false; else lift.Right = false;
                if (side != null)
                {
                    if (left) side.Right = false; else side.Left = false;
                }
                return;
            }
            Cell? vert = plan.At(lift.X, lift.Y + (top ? 1 : -1));
            if (top) lift.Top = false; else lift.Bottom = false;
            if (vert != null)
            {
                if (top) vert.Bottom = false; else vert.Top = false;
            }
        }

        // Unlink(top: false) means the bottom side; spelled out so the call sites read.
        private static void UnlinkBottom(FloorPlan plan, Cell lift) => Unlink(plan, lift, top: false);

        // Same rules the game uses on the ground floor: a leaf is a dead end turned to face its
        // one neighbour, a straight run is a passage while the level still allows them, the
        // rest are normal rooms at a random quarter turn.
        private static void Classify(Cell cell, ref int passages, int passageMax, Random rng)
        {
            if (cell.Connections == 1)
            {
                cell.Kind = Kind.DeadEnd;
                cell.RotationY = cell.Bottom ? 0f : cell.Left ? 90f : cell.Top ? 180f : 270f;
                return;
            }
            if (passages < passageMax && cell.Top && cell.Bottom && !cell.Left && !cell.Right)
            {
                cell.Kind = Kind.Passage;
                cell.RotationY = rng.Next(2) == 0 ? 0f : 180f;
                passages++;
                return;
            }
            if (passages < passageMax && cell.Left && cell.Right && !cell.Top && !cell.Bottom)
            {
                cell.Kind = Kind.Passage;
                cell.RotationY = rng.Next(2) == 0 ? 90f : 270f;
                passages++;
                return;
            }
            cell.Kind = Kind.Normal;
            cell.RotationY = 90f * rng.Next(4);
        }

        private static int Reachable(bool[,] ground, int sx, int sy)
        {
            int width = ground.GetLength(0);
            int height = ground.GetLength(1);
            var seen = new bool[width, height];
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sy));
            seen[sx, sy] = true;
            int n = 0;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                n++;
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || seen[nx, ny] || !ground[nx, ny])
                        continue;
                    seen[nx, ny] = true;
                    stack.Push((nx, ny));
                }
            }
            return n;
        }
    }
}
