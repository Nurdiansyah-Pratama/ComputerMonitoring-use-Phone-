using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class ShortcutCloseTests
{
    [Fact]
    public void IsClosable_SkipsBuiltinActionsAndAllowsApps()
    {
        Assert.False(Shortcuts.IsClosable(new Shortcut { Cmd = "builtin:mute" }));
        Assert.False(Shortcuts.IsClosable(new Shortcut { Cmd = "builtin:lock" }));
        Assert.False(Shortcuts.IsClosable(new Shortcut { Cmd = "builtin:monitor-off" }));
        Assert.True(Shortcuts.IsClosable(new Shortcut { Cmd = "wt.exe" }));
        Assert.True(Shortcuts.IsClosable(new Shortcut { Cmd = "https://github.com" }));
    }

    [Theory]
    [InlineData("wt.exe", null, "WindowsTerminal")]
    [InlineData("explorer.exe", null, "__explorer_windows__")]
    [InlineData(@"C:\Users\asus\AppData\Local\Programs\Microsoft VS Code\Code.exe", null, "Code")]
    [InlineData("https://github.com", "msedge", "msedge")]
    [InlineData("shell:AppsFolder\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", null, "Spotify")]
    [InlineData("calc.exe", null, "CalculatorApp")]
    [InlineData("ms-settings:", null, "SystemSettings")]
    [InlineData("ms-screenclip:", null, "SnippingTool")]
    [InlineData("taskmgr.exe", null, "Taskmgr")]
    public void ResolveCloseTargets_MapsAppsSafely(string cmd, string? browser, string expectedFirst)
    {
        var targets = Shortcuts.ResolveCloseTargets(cmd, browser);
        Assert.NotEmpty(targets);
        Assert.Equal(expectedFirst, targets[0]);
    }
}
