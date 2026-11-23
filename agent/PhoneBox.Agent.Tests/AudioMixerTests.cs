using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class AudioMixerTests
{
    [Theory]
    [InlineData("Headphones (Sony WH-1000XM5)", "bluetooth")]
    [InlineData("Galaxy Buds2 Pro Stereo", "bluetooth")]
    [InlineData("ROG Cetra Headset", "headphones")]
    [InlineData("LG HDR 4K (NVIDIA High Definition Audio)", "hdmi")]
    [InlineData("Speakers (Realtek(R) Audio)", "speaker")]
    public void ClassifyDeviceKind_DetectsOutputTypes(string name, string expected)
    {
        Assert.Equal(expected, AudioMixer.ClassifyDeviceKind(name));
    }

    [Theory]
    [InlineData("msedge", "Edge")]
    [InlineData("spotify", "Spotify")]
    [InlineData("discord", "Discord")]
    [InlineData("code", "VS Code")]
    [InlineData("obs64", "OBS")]
    public void FriendlyAppName_FormatsProcessNames(string proc, string expected)
    {
        Assert.Equal(expected, AudioMixer.FriendlyAppName(proc));
    }

    [Fact]
    public void Lan_AllowsAudioAndClipboardOverWifi()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/audio/mixer")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/audio/output")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/audio/app")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/clipboard")));
    }

    [Fact]
    public void AsusControl_AdaptiveRefresh_IsOffByDefaultAndOptInOnly()
    {
        Assert.Equal(120, AsusControl.PickAdaptiveHz([60, 120], onAcPower: true));
        Assert.Equal(60, AsusControl.PickAdaptiveHz([60, 120], onAcPower: false));
        Assert.Equal(3, AsusControl.DecodeKeyboardBacklight(0x00350183u));
        Assert.Equal(0, AsusControl.DecodeKeyboardBacklight(0x00350100u));
        Assert.Equal(0x82u, AsusControl.EncodeKeyboardBacklight(2));
    }
}
