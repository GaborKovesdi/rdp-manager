using RdpManager.Models;

namespace RdpManager.Tests;

public class UniqueNameTests
{
    private static List<RdpConnection> Named(params string[] names) =>
        names.Select(n => new RdpConnection { Name = n }).ToList();

    [Theory]
    [InlineData("srv", new string[0], "srv")]
    [InlineData("srv", new[] { "other" }, "srv")]
    [InlineData("srv", new[] { "srv" }, "srv (2)")]
    [InlineData("srv", new[] { "srv", "srv (2)" }, "srv (3)")]
    [InlineData("srv", new[] { "srv", "srv (3)" }, "srv (2)")]
    [InlineData("SRV", new[] { "srv" }, "SRV (2)")]
    public void UniqueName_NumbersATakenName(string name, string[] existing, string expected) =>
        Assert.Equal(expected, MainWindow.UniqueName(name, Named(existing)));
}
