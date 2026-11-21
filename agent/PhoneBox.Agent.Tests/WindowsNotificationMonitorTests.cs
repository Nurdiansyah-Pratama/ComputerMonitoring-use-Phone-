using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class WindowsNotificationMonitorTests
{
    [Fact]
    public void ParseToastXml_ExtractsTitleAndBodyAndStripsBidiMarkers()
    {
        var xml = "<toast><visual><binding template=\"ToastGeneric\"><text>CSSMoRA UIN SGD</text><text>Irsal\u200E: Sticker\u200E</text><text>13 unread</text></binding></visual></toast>";
        var (title, text) = WindowsNotificationMonitor.ParseToastXml(xml);
        Assert.Equal("CSSMoRA UIN SGD", title);
        Assert.Equal("Irsal: Sticker · 13 unread", text);
    }

    [Theory]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "WhatsApp")]
    [InlineData("electron.app.Antigravity", "Antigravity")]
    [InlineData("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!https://www.youtube.com/", "Edge · youtube.com")]
    [InlineData("Windows.Defender.SecurityCenter", "Windows Security")]
    public void FormatAppName_ProducesReadableLabels(string primaryId, string expected)
    {
        Assert.Equal(expected, WindowsNotificationMonitor.FormatAppName(primaryId));
    }

    [Fact]
    public void ShouldSkipApp_FiltersSpotifyMediaToasts()
    {
        Assert.True(WindowsNotificationMonitor.ShouldSkipApp("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"));
        Assert.False(WindowsNotificationMonitor.ShouldSkipApp("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"));
    }
}
