using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Tests;

public class AddressTests
{
    [Theory]
    [InlineData("srv", "srv", 3389)]
    [InlineData("srv:3390", "srv", 3390)]
    [InlineData("fe80::1", "fe80::1", 3389)]
    [InlineData("[fe80::1]", "[fe80::1]", 3389)]
    [InlineData("[fe80::1]:3390", "[fe80::1]", 3390)]
    public void ParseAddress_SplitsOffThePort(string value, string host, int port) =>
        Assert.Equal((host, port), RdpFileService.ParseAddress(value));

    [Theory]
    [InlineData("srv", 3389, "srv")]
    [InlineData("srv", 3390, "srv:3390")]
    [InlineData("fe80::1", 3390, "[fe80::1]:3390")]
    [InlineData("[fe80::1]", 3389, "[fe80::1]")]
    public void GetAddress_BuildsFullAddress(string host, int port, string expected) =>
        Assert.Equal(expected, RdpFileService.GetAddress(new RdpConnection { Host = host, Port = port }));
}
