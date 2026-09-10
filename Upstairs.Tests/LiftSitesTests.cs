using Upstairs.Services;
using Xunit;

namespace Upstairs.Tests
{
    public class LiftSitesTests
    {
        [Fact]
        public void MuseumUsesTheElevatorLanding()
        {
            LiftSite? site = LiftSites.For("Level - Museum");
            Assert.NotNull(site);
            Assert.Equal(FloorPlan.Kind.Passage, site!.Kind);
            Assert.False(site.PadAtHighestPoint);
            Assert.Equal("in the elevator", site.Hint);
            Assert.True(site.Shaft);
        }

        [Theory]
        [InlineData("Level - Shop")]
        [InlineData("Level - Manor")]
        [InlineData("Level - Arctic")]
        [InlineData("Some Modded Level")]
        [InlineData("")]
        [InlineData(null)]
        public void UnknownLevelsFallBack(string? level)
        {
            Assert.Null(LiftSites.For(level));
        }

        [Fact]
        public void AvoidListMatchesFragmentsAnywhereInTheName()
        {
            var avoid = RoomFilter.Parse(" Bottomless, Outside ,,");
            Assert.Equal(2, avoid.Count);
            Assert.True(RoomFilter.Avoids("Module - Manor - DE - 3 - Bottomless basement", avoid));
            Assert.True(RoomFilter.Avoids("Module - Manor - DE - 3 - outside deathpit", avoid));
            Assert.False(RoomFilter.Avoids("Module - Manor - N - 3 - Long Stairs", avoid));
            Assert.Empty(RoomFilter.Parse(null));
        }
    }
}
