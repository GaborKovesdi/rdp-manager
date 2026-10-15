using System.IO;
using System.Text.Json.Nodes;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Tests;

public class ConnectionStoreTests
{
    private const string MasterPassword = "correct horse battery";

    [Fact]
    public void Store_OpensOnlyWithTheRightPasswordAndUnalteredFile()
    {
        // The store always lives next to the running assembly, here the test output folder.
        var dataFile = Path.Combine(AppContext.BaseDirectory, "data", "connections.dat");
        if (File.Exists(dataFile))
            File.Delete(dataFile);

        using (var store = new ConnectionStore())
        {
            store.Initialize(MasterPassword);
            store.Save(new[] { new RdpConnection { Name = "a", Host = "srv", Password = "secret" } });
        }

        using (var store = new ConnectionStore())
        {
            Assert.Equal(UnlockResult.WrongPassword, store.TryUnlock("wrong password", out _));
            Assert.Equal(UnlockResult.Success, store.TryUnlock(MasterPassword, out var loaded));
            Assert.Equal("secret", Assert.Single(loaded).Password);
        }

        var envelope = JsonNode.Parse(File.ReadAllText(dataFile))!;
        var ciphertext = Convert.FromBase64String((string)envelope["Ciphertext"]!);
        ciphertext[0] ^= 1;
        envelope["Ciphertext"] = Convert.ToBase64String(ciphertext);
        File.WriteAllText(dataFile, envelope.ToJsonString());

        using (var store = new ConnectionStore())
            Assert.Equal(UnlockResult.WrongPassword, store.TryUnlock(MasterPassword, out _));
    }
}
