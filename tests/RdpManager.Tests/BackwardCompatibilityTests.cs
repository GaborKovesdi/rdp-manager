using System.IO;
using RdpManager.Services;

namespace RdpManager.Tests;

/// <summary>
/// Every version must keep opening the stores earlier releases wrote. The fixtures were written by
/// the released builds themselves: never regenerate or edit them, add a new one from a later release.
/// </summary>
public class BackwardCompatibilityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void StoreWrittenByV1_0_0_StillOpens()
    {
        using var store = new ConnectionStore(Fixture("connections-v1.0.0.dat"));

        Assert.Equal(UnlockResult.Success, store.TryUnlock("backward-compat-1", out var connections));
        Assert.Collection(connections,
            c =>
            {
                Assert.Equal("Régi szerver", c.Name);
                Assert.Equal("old.example.local", c.Host);
                Assert.Equal(3390, c.Port);
                Assert.Equal("CORP", c.Domain);
                Assert.Equal("anna", c.Username);
                Assert.Equal("jelszó-1", c.Password);
                Assert.Null(c.PasswordSavedAt);
                Assert.Equal("screen mode id:i:2\r\ndesktopwidth:i:1280", c.RawRdpContent);
            },
            c =>
            {
                Assert.Equal("IPv6 gép", c.Name);
                Assert.Equal("[fe80::1]", c.Host);
                Assert.Equal(3389, c.Port);
                Assert.Equal("root", c.Username);
                Assert.Equal("p2", c.Password);
            });
    }
}
