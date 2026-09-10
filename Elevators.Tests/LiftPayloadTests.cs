using Elevators.Services;
using Xunit;

namespace Elevators.Tests
{
    public class LiftPayloadTests
    {
        [Fact]
        public void RoundTrips()
        {
            var p = new LiftPayload { Nonce = 42, Level = "Level - Museum", GroundTop = 31.1f, GroundBottom = -20.05f, HasUp = true, HasDown = false };
            p.Pads.Add(new LiftPayload.Pad { Name = "ground", X = -4f, Y = 0f, Z = 24.4f, Yaw = 180f });
            p.Pads.Add(new LiftPayload.Pad { Name = "upstairs", X = 1.01f, Y = 40f, Z = 22.57f, Yaw = 90f });

            LiftPayload? back = LiftPayload.Parse(p.Pack());

            Assert.NotNull(back);
            Assert.Equal(42, back!.Nonce);
            Assert.Equal("Level - Museum", back.Level);
            Assert.Equal(31.1f, back.GroundTop, 2);
            Assert.Equal(-20.05f, back.GroundBottom, 2);
            Assert.True(back.HasUp);
            Assert.False(back.HasDown);
            Assert.Equal(2, back.Pads.Count);
            Assert.Equal("upstairs", back.Pads[1].Name);
            Assert.Equal(22.57f, back.Pads[1].Z, 2);
            Assert.Equal(90f, back.Pads[1].Yaw, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("1|Level|x|0|10")]
        [InlineData("1|Level|0|0|10|ground:1,2")]
        public void RejectsJunk(string? text)
        {
            Assert.Null(LiftPayload.Parse(text));
        }

        [Fact]
        public void NoPadsIsStillValid()
        {
            LiftPayload? back = LiftPayload.Parse(new LiftPayload { Nonce = 1, Level = "L", HasDown = true }.Pack());
            Assert.NotNull(back);
            Assert.Empty(back!.Pads);
            Assert.True(back.HasDown);
        }
    }
}
