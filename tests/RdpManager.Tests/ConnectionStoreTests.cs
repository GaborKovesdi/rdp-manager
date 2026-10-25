using System.IO;
using System.Text.Json.Nodes;
using RdpManager.Models;
using RdpManager.Services;

namespace RdpManager.Tests;

// All tests share the one store file next to the test assembly; xUnit runs a class's tests one at a time.
public class ConnectionStoreTests
{
    private const string MasterPassword = "correct horse battery";
    private const string NewPassword = "new master password";

    private static readonly string DataFile = Path.Combine(AppContext.BaseDirectory, "data", "connections.dat");

    /// <summary>Starts every test from a fresh store holding one connection, and returns that store unlocked.</summary>
    private static ConnectionStore CreateStore(out List<RdpConnection> connections)
    {
        if (File.Exists(DataFile))
            File.Delete(DataFile);

        var store = new ConnectionStore();
        store.Initialize(MasterPassword);
        connections = new List<RdpConnection>
        {
            new() { Name = "a", Host = "srv", Password = "secret", PasswordSavedAt = new DateTimeOffset(2026, 10, 25, 14, 32, 0, TimeSpan.FromHours(2)) },
        };
        store.Save(connections);
        return store;
    }

    private static UnlockResult Unlock(string password, out List<RdpConnection> connections)
    {
        using var store = new ConnectionStore();
        return store.TryUnlock(password, out connections);
    }

    [Fact]
    public void Store_OpensOnlyWithTheRightPasswordAndUnalteredFile()
    {
        CreateStore(out _).Dispose();

        Assert.Equal(UnlockResult.WrongPassword, Unlock("wrong password", out _));
        Assert.Equal(UnlockResult.Success, Unlock(MasterPassword, out var loaded));
        var connection = Assert.Single(loaded);
        Assert.Equal("secret", connection.Password);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 14, 32, 0, TimeSpan.FromHours(2)), connection.PasswordSavedAt);

        var envelope = JsonNode.Parse(File.ReadAllText(DataFile))!;
        var ciphertext = Convert.FromBase64String((string)envelope["Ciphertext"]!);
        ciphertext[0] ^= 1;
        envelope["Ciphertext"] = Convert.ToBase64String(ciphertext);
        File.WriteAllText(DataFile, envelope.ToJsonString());

        Assert.Equal(UnlockResult.WrongPassword, Unlock(MasterPassword, out _));
    }

    [Fact]
    public void ChangeMasterPassword_NeedsTheCurrentPasswordAndReplacesIt()
    {
        using (var store = CreateStore(out var connections))
        {
            Assert.False(store.ChangeMasterPassword("wrong password", NewPassword, connections));
            Assert.True(store.ChangeMasterPassword(MasterPassword, NewPassword, connections));
        }

        Assert.Equal(UnlockResult.WrongPassword, Unlock(MasterPassword, out _));
        Assert.Equal(UnlockResult.Success, Unlock(NewPassword, out var loaded));
        Assert.Equal("secret", Assert.Single(loaded).Password);
    }

    [Fact]
    public void ChangeMasterPassword_KeepsTheOldPasswordWhenTheSaveFails()
    {
        using (var store = CreateStore(out var connections))
        {
            File.SetAttributes(DataFile, FileAttributes.ReadOnly);
            try
            {
                var error = Record.Exception(() => store.ChangeMasterPassword(MasterPassword, NewPassword, connections));
                Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error}");
            }
            finally
            {
                File.SetAttributes(DataFile, FileAttributes.Normal);
            }

            // A later ordinary save must not quietly switch to the password whose save failed.
            store.Save(connections);
        }

        Assert.Equal(UnlockResult.Success, Unlock(MasterPassword, out _));
    }
}
