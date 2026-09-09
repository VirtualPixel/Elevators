using Upstairs.Services;
using Xunit;

namespace Upstairs.Tests
{
    public class FloorGapTests
    {
        [Theory]
        [InlineData(25.7f, -28f, 60f)]
        [InlineData(4f, -4f, 16f)]
        [InlineData(0f, 0f, 12f)]
        [InlineData(29.7f, -12.1f, 48f)]
        public void ClearsTheTallestRoomAndSnapsToMapLayers(float lowerTop, float upperBottom, float expected)
        {
            Assert.Equal(expected, FloorGap.Compute(lowerTop, upperBottom));
        }

        [Fact]
        public void GapIsAlwaysAWholeMapLayer()
        {
            for (float top = 0f; top < 40f; top += 1.3f)
                Assert.Equal(0f, FloorGap.Compute(top, -7.7f) % FloorGap.MapLayer);
        }

        [Theory]
        [InlineData(10, 60, 6)]
        [InlineData(5, 60, 3)]
        [InlineData(2, 20, 2)]
        [InlineData(15, 100, 15)]
        public void CellCountFollowsTheShare(int ground, int percent, int expected)
        {
            Assert.Equal(expected, FloorGap.CellCount(ground, percent));
        }
    }
}
