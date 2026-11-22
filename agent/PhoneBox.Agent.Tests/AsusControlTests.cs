using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class AsusControlTests
{
    [Theory]
    [InlineData("whisper", "whisper")]
    [InlineData("Silent", "whisper")]
    [InlineData("standard", "standard")]
    [InlineData("Balanced", "standard")]
    [InlineData("performance", "performance")]
    [InlineData("TURBO", "performance")]
    [InlineData("unknown", null)]
    public void NormalizeFanMode_MapsAliases(string input, string? expected)
    {
        Assert.Equal(expected, AsusControl.NormalizeFanMode(input));
    }

    [Fact]
    public void VivoFanMode_RoundTripsCorrectly()
    {
        Assert.Equal(0u, AsusControl.EncodeVivoFanMode("standard"));
        Assert.Equal(1u, AsusControl.EncodeVivoFanMode("whisper"));
        Assert.Equal(2u, AsusControl.EncodeVivoFanMode("performance"));
        Assert.Equal("standard", AsusControl.DecodeVivoFanMode(0));
        Assert.Equal("whisper", AsusControl.DecodeVivoFanMode(1));
        Assert.Equal("performance", AsusControl.DecodeVivoFanMode(2));
    }
}
