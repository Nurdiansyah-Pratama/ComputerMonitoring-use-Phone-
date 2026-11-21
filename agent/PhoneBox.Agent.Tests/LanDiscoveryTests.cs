using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class LanDiscoveryTests
{
    [Fact]
    public void BuildReply_IncludesPublicMetadataAndNoSecrets()
    {
        var bytes = LanDiscovery.BuildReply("MY-PC", "abcdef0123456789", 8766, ["192.168.1.3", "100.64.0.1"]);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("MY-PC", root.GetProperty("pc").GetString());
        Assert.Equal("abcdef0123456789", root.GetProperty("fp").GetString());
        Assert.Equal(8766, root.GetProperty("port").GetInt32());
        Assert.Equal(["192.168.1.3", "100.64.0.1"], root.GetProperty("hosts").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(root.TryGetProperty("token", out _));
        Assert.False(root.TryGetProperty("webhookToken", out _));
    }
}
