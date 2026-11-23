using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData("default", "default")]
    [InlineData("nothing", "nothing")]
    [InlineData("NOTHING", "nothing")]
    [InlineData("cyberpunk", "cyberpunk")]
    [InlineData("macglass", "macglass")]
    [InlineData("MacGlass", "macglass")]
    [InlineData("industrial", "industrial")]
    [InlineData("amoled", "amoled")]
    [InlineData("midnight", "midnight")]
    [InlineData("warm", "warm")]
    [InlineData("graphite", "graphite")]
    [InlineData("unknown-style", "default")]
    [InlineData("", "default")]
    public void ConfigStore_NormalizesDisplayThemes(string input, string expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pb-theme-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "config.json");
            var store = new ConfigStore(path);
            var cfg = store.Get();
            cfg.Display.Theme = input;
            var err = store.Save(JsonSerializer.Serialize(cfg, ConfigStore.WriteOpts));
            Assert.Null(err);
            Assert.Equal(expected, store.Get().Display.Theme);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Lan_AllowsThemeEndpointOverWifi()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/theme")));
    }

    [Fact]
    public void EmbeddedResources_IncludeThemesCss()
    {
        using var stream = AppPaths.Resource("www/themes.css");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var css = reader.ReadToEnd();
        Assert.Contains("body[data-theme=\"nothing\"]", css);
        Assert.Contains("body[data-theme=\"cyberpunk\"]", css);
        Assert.Contains("body[data-theme=\"macglass\"]", css);
        Assert.Contains("body[data-theme=\"industrial\"]", css);
    }
}
