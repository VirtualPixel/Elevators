using System;
using System.Collections.Generic;
using System.Linq;
using Upstairs.Services;
using Xunit;

namespace Upstairs.Tests
{
    public class FloorPlanTests
    {
        // A plus shape with a tail, the kind of grid the game's random walk makes.
        private static bool[,] Ground()
        {
            var g = new bool[5, 5];
            g[2, 0] = true; g[2, 1] = true; g[2, 2] = true; g[1, 2] = true; g[3, 2] = true; g[2, 3] = true; g[2, 4] = true; g[0, 2] = true;
            return g;
        }

        [Fact]
        public void OnlyStacksOnActiveGroundCells()
        {
            bool[,] ground = Ground();
            FloorPlan plan = FloorPlan.Build(ground, 2, 2, 5, 2, new Random(1));
            Assert.Equal(5, plan.Cells.Count);
            Assert.All(plan.Cells, c => Assert.True(ground[c.X, c.Y]));
        }

        [Fact]
        public void StaysConnectedThroughTheLiftCell()
        {
            FloorPlan plan = FloorPlan.Build(Ground(), 2, 2, 6, 2, new Random(7));
            var seen = new HashSet<(int, int)> { (2, 2) };
            var stack = new Stack<(int, int)>();
            stack.Push((2, 2));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (plan.At(nx, ny) != null && seen.Add((nx, ny)))
                        stack.Push((nx, ny));
                }
            }
            Assert.Equal(plan.Cells.Count, seen.Count);
        }

        [Fact]
        public void CapsAtWhatTheGroundCanHold()
        {
            FloorPlan plan = FloorPlan.Build(Ground(), 2, 2, 50, 2, new Random(3));
            Assert.Equal(8, plan.Cells.Count);
        }

        [Fact]
        public void LeavesAreDeadEndsFacingTheirNeighbour()
        {
            var g = new bool[3, 3];
            g[1, 0] = true; g[1, 1] = true; g[1, 2] = true;
            FloorPlan plan = FloorPlan.Build(g, 1, 1, 3, 0, new Random(2));
            FloorPlan.Cell? low = plan.At(1, 0);
            FloorPlan.Cell? high = plan.At(1, 2);
            Assert.NotNull(low);
            Assert.NotNull(high);
            Assert.Equal(FloorPlan.Kind.DeadEnd, low!.Kind);
            Assert.Equal(180f, low.RotationY);
            Assert.Equal(FloorPlan.Kind.DeadEnd, high!.Kind);
            Assert.Equal(0f, high.RotationY);
        }

        [Fact]
        public void StraightRunsBecomePassagesUntilTheBudgetRunsOut()
        {
            var g = new bool[1, 6];
            for (int y = 0; y < 6; y++) g[0, y] = true;
            FloorPlan plan = FloorPlan.Build(g, 0, 0, 6, 1, new Random(5));
            List<FloorPlan.Cell> middle = plan.Cells.Where(c => c.Connections == 2).ToList();
            Assert.Equal(4, middle.Count);
            Assert.Single(middle.Where(c => c.Kind == FloorPlan.Kind.Passage));
            Assert.All(middle.Where(c => c.Kind == FloorPlan.Kind.Passage), c => Assert.True(c.RotationY == 0f || c.RotationY == 180f));
            Assert.All(middle.Where(c => c.Kind == FloorPlan.Kind.Normal), c => Assert.True(c.RotationY % 90f == 0f));
        }

        [Fact]
        public void SidewaysPassagesTurnNinety()
        {
            var g = new bool[3, 1];
            g[0, 0] = true; g[1, 0] = true; g[2, 0] = true;
            FloorPlan plan = FloorPlan.Build(g, 1, 0, 3, 2, new Random(9));
            FloorPlan.Cell mid = plan.At(1, 0)!;
            Assert.Equal(FloorPlan.Kind.Passage, mid.Kind);
            Assert.True(mid.RotationY == 90f || mid.RotationY == 270f);
        }

        [Fact]
        public void OneDoorPerSharedEdge()
        {
            FloorPlan plan = FloorPlan.Build(Ground(), 2, 2, 8, 2, new Random(4));
            int edges = plan.Cells.Sum(c => c.Connections) / 2;
            Assert.Equal(edges, plan.Doors.Count);
            Assert.All(plan.Doors, d =>
            {
                FloorPlan.Cell? other = d.RightEdge ? plan.At(d.X + 1, d.Y) : plan.At(d.X, d.Y + 1);
                Assert.NotNull(other);
            });
        }

        [Fact]
        public void SameSeedSamePlan()
        {
            FloorPlan a = FloorPlan.Build(Ground(), 2, 2, 5, 2, new Random(11));
            FloorPlan b = FloorPlan.Build(Ground(), 2, 2, 5, 2, new Random(11));
            Assert.Equal(a.Cells.Select(c => (c.X, c.Y, c.Kind, c.RotationY)), b.Cells.Select(c => (c.X, c.Y, c.Kind, c.RotationY)));
        }

        [Fact]
        public void LiftOffTheGroundMakesNoPlan()
        {
            FloorPlan plan = FloorPlan.Build(Ground(), 0, 0, 4, 2, new Random(1));
            Assert.Empty(plan.Cells);
        }
    }
}

namespace Upstairs.Tests
{
    public class FloorPlanLiftAxisTests
    {
        [Fact]
        public void PinnedLiftCellIsAPassageWithSideDoorsRemoved()
        {
            var g = new bool[3, 3];
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) g[x, y] = true;
            FloorPlan plan = FloorPlan.Build(g, 1, 1, 9, 5, new Random(3), liftAxisVertical: true);
            FloorPlan.Cell lift = plan.At(1, 1)!;
            Assert.Equal(FloorPlan.Kind.Passage, lift.Kind);
            Assert.Equal(0f, lift.RotationY);
            Assert.False(lift.Left);
            Assert.False(lift.Right);
            Assert.False(plan.At(0, 1)!.Right);
            Assert.False(plan.At(2, 1)!.Left);
            Assert.DoesNotContain(plan.Doors, d => d.RightEdge && d.Y == 1 && (d.X == 0 || d.X == 1));
        }

        [Fact]
        public void HorizontalPinTurnsTheLift()
        {
            var g = new bool[3, 3];
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) g[x, y] = true;
            FloorPlan plan = FloorPlan.Build(g, 1, 1, 9, 5, new Random(3), liftAxisVertical: false);
            FloorPlan.Cell lift = plan.At(1, 1)!;
            Assert.Equal(90f, lift.RotationY);
            Assert.False(lift.Top);
            Assert.False(lift.Bottom);
            Assert.False(plan.At(1, 2)!.Bottom);
            Assert.False(plan.At(1, 0)!.Top);
        }
    }
}

namespace Upstairs.Tests
{
    public class FloorPlanOpenSideTests
    {
        [Fact]
        public void TheOpenSideStaysFreeOfRoomsAndDoors()
        {
            var g = new bool[3, 3];
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) g[x, y] = true;
            FloorPlan plan = FloorPlan.Build(g, 1, 1, 9, 2, new Random(8), liftOpenSide: 1);
            FloorPlan.Cell lift = plan.At(1, 1)!;
            Assert.False(lift.Right);
            Assert.False(plan.At(2, 1)!.Left);
            Assert.DoesNotContain(plan.Doors, d => d.RightEdge && d.X == 1 && d.Y == 1);
        }
    }
}
