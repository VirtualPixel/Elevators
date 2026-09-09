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
            Assert.Equal("at the elevator doors", site.Hint);
        }

        [Theory]
        [InlineData("Level - Manor")]
        [InlineData("Level - Arctic")]
        [InlineData("Level - Wizard")]
        public void StairLevelsUseTheirTopLanding(string level)
        {
            LiftSite? site = LiftSites.For(level);
            Assert.NotNull(site);
            Assert.True(site!.PadAtHighestPoint);
        }

        [Theory]
        [InlineData("Level - Shop")]
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
