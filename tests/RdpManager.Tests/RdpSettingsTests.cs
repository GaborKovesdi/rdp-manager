using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Tests;

public class RdpSettingsTests
{
    [Fact]
    public void ParseSettingLines_DropsBlankLinesAndTheKeysTheLauncherSets() =>
        Assert.Equal(
            new[] { "desktopwidth:i:1280", "gatewayhostname:s:gw.example.local" },
            RdpFileService.ParseSettingLines(
                "full address:s:srv\r\n\r\n  desktopwidth:i:1280  \nUsername:s:anna\npassword 51:b:01ABCD\ngatewayhostname:s:gw.example.local\n"));

    [Fact]
    public void GetSettingLines_FallsBackToTheDefaults() =>
        Assert.Contains("screen mode id:i:2", RdpFileService.GetSettingLines(new RdpConnection()));

    [Fact]
    public void BuildRdpContent_WritesTheAddressOnceOverEditedSettings()
    {
        var content = RdpFileService.BuildRdpContent(new RdpConnection
        {
            Host = "new.example.local",
            RawRdpContent = "full address:s:old.example.local\r\ndesktopwidth:i:1280",
        });

        Assert.Equal(new[] { "desktopwidth:i:1280", "full address:s:new.example.local", "username:s:", "prompt for credentials:i:0" },
            content.Split("\r\n"));
    }

    [Theory]
    [InlineData(new[] { "desktopwidth:i:1280", "gatewayhostname:s:", "x:b:00" }, null)]
    [InlineData(new[] { "desktopwidth:i:1280", "desktopwidth 1280" }, "desktopwidth 1280")]
    [InlineData(new[] { "desktopwidth:x:1280" }, "desktopwidth:x:1280")]
    [InlineData(new[] { " :i:1" }, " :i:1")]
    public void FindInvalidSettingLine_FlagsWhatIsNotKeyTypeValue(string[] lines, string? expected) =>
        Assert.Equal(expected, RdpFileService.FindInvalidSettingLine(lines));

    [Fact]
    public void Clone_IsIndependentOfTheOriginal()
    {
        var original = new RdpConnection { Name = "srv", Host = "srv", Password = "pw", RawRdpContent = "desktopwidth:i:1280" };
        var copy = original.Clone();
        copy.Host = "other";

        Assert.Equal(("srv", "pw", "desktopwidth:i:1280"), (original.Host, copy.Password, copy.RawRdpContent));
    }
}
